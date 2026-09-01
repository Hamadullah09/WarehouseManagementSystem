using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Documents;
using Warehouse.Domain;
using Warehouse.Domain.Entities;
using Xunit;

namespace Warehouse.Application.Tests;

/// <summary>
/// End-to-end gate behaviour: signal, read, validate, commit (§39, §48).
/// </summary>
/// <remarks>
/// Each test drives the simulated reader's GPIO and tag callbacks and then
/// asserts on the database, because that is where the warehouse's real answer
/// lives. Nothing is stubbed between the reader adapter and the committed row.
/// </remarks>
public class GateCycleTests
{
    private static string[] Epcs(int count, int start = 1) =>
        Enumerable.Range(start, count).Select(i => $"E2801160{i:D8}").ToArray();

    private static async Task<DocumentDetailDto> CreateDocumentAsync(
        WarehouseTestHost host,
        DocumentType type,
        IReadOnlyList<string> epcs)
    {
        using var scope = host.Scope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

        return await documents.CreateAsync(type, new CreateDocumentRequest
        {
            Epcs = epcs,
            GateCode = WarehouseTestHost.GateCode
        });
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task Full_inward_cycle_passes_and_commits_inventory()
    {
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        document.DocumentNumber.Should().MatchRegex(@"^IN-\d{4}-\d{6}$");
        document.ExpectedArticles.Should().Be(30);
        document.ExpectedQuantity.Should().Be(120);

        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var completed = host.Notifier.CycleCompleted.Should().ContainSingle().Subject;
        completed.Passed.Should().BeTrue();
        completed.DetectedCount.Should().Be(30);
        completed.Missing.Should().BeEmpty();

        var stored = await host.WithDbAsync(db => db.Documents
            .Include(d => d.Items)
            .FirstAsync(d => d.Id == document.Id));

        stored.Status.Should().Be(DocumentStatus.Completed);
        stored.DetectedArticles.Should().Be(30);
        stored.DetectedQuantity.Should().Be(120);
        stored.Items.Should().OnlyContain(i => i.IsDetected);

        var tags = await host.WithDbAsync(db => db.EpcTags.ToListAsync());
        tags.Should().OnlyContain(t => t.Status == EpcStatus.InStock);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(30);
    }

    [Fact]
    public async Task Full_outward_cycle_ships_stock()
    {
        var epcs = Epcs(5);

        await using var host = await new WarehouseTestHost().StartAsync(epcs, EpcStatus.InStock);

        await CreateDocumentAsync(host, DocumentType.Outward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var tags = await host.WithDbAsync(db => db.EpcTags.ToListAsync());
        tags.Should().OnlyContain(t => t.Status == EpcStatus.Shipped);
    }

    // --------------------------------------------------------- deduplication

    [Fact]
    public async Task Repeated_reads_produce_one_movement_per_tag()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // Six reads of each tag, exactly the case in the brief (§12).
        await host.RunCycleAsync(epcs, repeats: 6);

        var cycle = await host.WithDbAsync(db => db.GateCycles.FirstAsync());
        cycle.RawReadCount.Should().Be(18);
        cycle.DetectedEpcCount.Should().Be(3);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(3);

        var cycleEpcs = await host.WithDbAsync(db => db.GateCycleEpcs.CountAsync());
        cycleEpcs.Should().Be(3);
    }

    // ----------------------------------------------------------- alarm paths

    [Fact]
    public async Task Unknown_epc_raises_an_alarm_and_blocks_the_movement()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // "DEADBEEF" exists nowhere in the catalogue.
        await host.RunCycleAsync(epcs.Append("DEADBEEF"));

        var alarms = await host.WithDbAsync(db => db.Alarms.ToListAsync());
        alarms.Should().Contain(a => a.AlarmType == AlarmType.UnknownEpc);
        alarms.First(a => a.AlarmType == AlarmType.UnknownEpc).Epc.Should().Be("DEADBEEF");

        var stored = await host.WithDbAsync(db => db.Documents.FirstAsync(d => d.Id == document.Id));
        stored.Status.Should().NotBe(DocumentStatus.Completed);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(0, "a cycle containing an unknown tag must not move any stock");

        var recorded = await host.WithDbAsync(db => db.GateCycleEpcs
            .Where(e => e.Classification == EpcClassification.Unknown)
            .ToListAsync());

        recorded.Should().ContainSingle().Which.Epc.Should().Be("DEADBEEF");
    }

    [Fact]
    public async Task Known_but_unexpected_epc_is_reported_separately_from_unknown()
    {
        var onDocument = Epcs(3);
        var stray = Epcs(1, start: 99).Single();

        await using var host = await new WarehouseTestHost()
            .StartAsync(onDocument.Append(stray));

        await CreateDocumentAsync(host, DocumentType.Inward, onDocument);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(onDocument.Append(stray));

        var alarms = await host.WithDbAsync(db => db.Alarms.ToListAsync());

        alarms.Should().Contain(a => a.AlarmType == AlarmType.UnexpectedEpc);
        alarms.Should().NotContain(a => a.AlarmType == AlarmType.UnknownEpc);

        alarms.First(a => a.AlarmType == AlarmType.UnexpectedEpc).Epc.Should().Be(stray);
    }

    [Fact]
    public async Task Missing_epc_reports_the_shortfall_and_names_the_tag()
    {
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // 29 of 30 pass the antenna.
        await host.RunCycleAsync(epcs.Take(29));

        var alarm = await host.WithDbAsync(db => db.Alarms
            .FirstAsync(a => a.AlarmType == AlarmType.MissingEpc));

        alarm.Message.Should().Contain("Expected 30");
        alarm.Message.Should().Contain("detected 29");
        alarm.Epc.Should().Be(epcs[29]);

        var cycle = await host.WithDbAsync(db => db.GateCycles.FirstAsync());
        cycle.MissingEpcCount.Should().Be(1);
        cycle.InventoryCommitted.Should().BeFalse();
    }

    [Fact]
    public async Task Cycle_with_no_reads_raises_the_untagged_item_alarm()
    {
        var epcs = Epcs(4);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // Signal on, nothing read, signal off: an item passed with no tag (§16, §17).
        await host.Reader.GpioOnAsync();
        await host.Reader.GpioOffAsync();

        var alarms = await host.WithDbAsync(db => db.Alarms.ToListAsync());
        alarms.Should().Contain(a => a.AlarmType == AlarmType.NoEpc);

        alarms.First(a => a.AlarmType == AlarmType.NoEpc)
            .Message.Should().Contain("without an RFID tag");
    }

    // -------------------------------------------------------- reader failure

    [Fact]
    public async Task Reader_error_during_a_cycle_prevents_a_pass()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        await host.Reader.GpioOnAsync();
        await host.Reader.EmitTagsAsync(epcs, 2);

        // Everything the document wanted was read, but the reader misbehaved.
        await host.Reader.RaiseErrorAsync("startInventoryTag", "Simulated SDK fault");

        await host.Reader.GpioOffAsync();

        var cycle = await host.WithDbAsync(db => db.GateCycles.FirstAsync());
        cycle.ReaderHealthy.Should().BeFalse();
        cycle.ValidationResult.Should().Be(ValidationOutcome.Fail);
        cycle.InventoryCommitted.Should().BeFalse();

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(0);
    }

    [Fact]
    public async Task Reader_disconnect_mid_cycle_aborts_rather_than_validating()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        await host.Reader.GpioOnAsync();
        await host.Reader.EmitTagsAsync(epcs, 1);
        await host.Reader.DisconnectAsync("Cable pulled");

        var cycle = await host.WithDbAsync(db => db.GateCycles.FirstAsync());
        cycle.Status.Should().Be(GateCycleStatus.Aborted);
        cycle.InventoryCommitted.Should().BeFalse();

        var alarms = await host.WithDbAsync(db => db.Alarms.ToListAsync());
        alarms.Should().Contain(a => a.AlarmType == AlarmType.ReaderDisconnected);

        var snapshot = await host.Gates.GetSnapshotAsync(WarehouseTestHost.GateCode);
        snapshot!.State.Should().Be(GateState.ReaderDisconnected);
        snapshot.ReaderOnline.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_signal_while_the_reader_is_offline_does_not_open_a_cycle()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.Reader.DisconnectAsync("Reader offline");

        await host.Reader.GpioOnAsync();
        await host.Reader.EmitTagsAsync(epcs, 1);
        await host.Reader.GpioOffAsync();

        var cycles = await host.WithDbAsync(db => db.GateCycles.CountAsync());
        cycles.Should().Be(0, "no cycle may open against an offline reader");

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(0);
    }

    [Fact]
    public async Task Reader_reconnect_returns_the_gate_to_service()
    {
        var epcs = Epcs(2);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Reader.DisconnectAsync("Transient failure");

        var offline = await host.Gates.GetSnapshotAsync(WarehouseTestHost.GateCode);
        offline!.State.Should().Be(GateState.ReaderDisconnected);

        await host.Reader.ReconnectAsync();

        var online = await host.Gates.GetSnapshotAsync(WarehouseTestHost.GateCode);
        online!.ReaderOnline.Should().BeTrue();
        online.State.Should().Be(GateState.Idle);

        // And the gate can be put back to work.
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var stored = await host.WithDbAsync(db => db.Documents.FirstAsync());
        stored.Status.Should().Be(DocumentStatus.Completed);
    }

    // ------------------------------------------------------------ idempotency

    [Fact]
    public async Task A_repeated_gate_edge_does_not_open_a_second_cycle()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        await host.Reader.GpioOnAsync();
        await host.Reader.GpioOnAsync(); // bounce
        await host.Reader.EmitTagsAsync(epcs, 2);
        await host.Reader.GpioOffAsync();

        var cycles = await host.WithDbAsync(db => db.GateCycles.CountAsync());
        cycles.Should().Be(1);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(3);
    }

    [Fact]
    public async Task Running_the_same_load_twice_does_not_double_count()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        await host.RunCycleAsync(epcs);
        await host.RunCycleAsync(epcs);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(3, "the document was already satisfied by the first pass");

        var document = await host.WithDbAsync(db => db.Documents.FirstAsync());
        document.DetectedArticles.Should().Be(3);
        document.DetectedQuantity.Should().Be(12);
    }

    // -------------------------------------------------------------- realtime

    [Fact]
    public async Task Display_receives_incremental_progress_during_a_cycle()
    {
        var epcs = Epcs(5);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs, repeats: 4);

        // One notification per distinct EPC, not per read.
        host.Notifier.EpcDetected.Should().HaveCount(5);
        host.Notifier.EpcDetected.Should().OnlyContain(e => e.IsExpected && e.IsKnown);
        host.Notifier.EpcDetected.Select(e => e.DetectedCount).Should().BeInAscendingOrder();
        host.Notifier.EpcDetected.Should().OnlyContain(e => e.ExpectedCount == 5);

        host.Notifier.GateStatus.Should().Contain(s => s.State == GateState.Reading);
        host.Notifier.CycleCompleted.Should().ContainSingle().Which.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_snapshot_reports_the_outstanding_balance()
    {
        var epcs = Epcs(10);

        await using var host = await new WarehouseTestHost(o => o.RequireAllExpected = false)
            .StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // Half the load goes through on the first pass.
        await host.RunCycleAsync(epcs.Take(6));

        var snapshot = await host.Gates.GetSnapshotAsync(WarehouseTestHost.GateCode);

        snapshot!.ExpectedArticles.Should().Be(10);
        snapshot.DetectedArticles.Should().Be(6);
        snapshot.BalanceArticles.Should().Be(4);
        snapshot.BalanceQuantity.Should().Be(16);
        snapshot.BalanceEpcs.Should().BeEquivalentTo(epcs.Skip(6));
    }

    // ----------------------------------------------------------------- audit

    [Fact]
    public async Task A_completed_cycle_leaves_a_full_audit_trail()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var actions = await host.WithDbAsync(db => db.AuditLogs.Select(a => a.Action).ToListAsync());

        actions.Should().Contain(AuditAction.DocumentCreated);
        actions.Should().Contain(AuditAction.GpioOn);
        actions.Should().Contain(AuditAction.GateCycleStarted);
        actions.Should().Contain(AuditAction.GateCycleCompleted);
        actions.Should().Contain(AuditAction.InventoryCommitted);
        actions.Should().Contain(AuditAction.DocumentCompleted);

        var gpio = await host.WithDbAsync(db => db.GpioEvents.ToListAsync());
        gpio.Should().Contain(e => e.IsInput && e.High);
        gpio.Should().Contain(e => e.IsInput && !e.High);
    }

    [Fact]
    public async Task Unknown_epc_is_recorded_in_the_audit_trail()
    {
        var epcs = Epcs(2);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs.Append("BADCAFE0"));

        var entry = await host.WithDbAsync(db => db.AuditLogs
            .FirstAsync(a => a.Action == AuditAction.UnknownEpc));

        entry.Epc.Should().Be("BADCAFE0");
        entry.Result.Should().Be("UNKNOWN_EPC");
    }
}
