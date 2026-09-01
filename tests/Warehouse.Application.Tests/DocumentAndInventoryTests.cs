using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Documents;
using Warehouse.Application.Epcs;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Entities;
using Xunit;

namespace Warehouse.Application.Tests;

/// <summary>Document lifecycle: numbering, validation, release, cancel, retry (§39).</summary>
public class DocumentServiceTests
{
    private static string[] Epcs(int count, int start = 1) =>
        Enumerable.Range(start, count).Select(i => $"E2801160{i:D8}").ToArray();

    private static IDocumentService Documents(WarehouseTestHost host, IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDocumentService>();

    [Fact]
    public async Task Document_numbers_are_sequential_and_typed()
    {
        var epcs = Epcs(6);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var first = await documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs.Take(2).ToList() });

        var second = await documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs.Skip(2).Take(2).ToList() });

        var year = DateTimeOffset.UtcNow.Year;

        first.DocumentNumber.Should().Be($"IN-{year}-000001");
        second.DocumentNumber.Should().Be($"IN-{year}-000002");

        // Outward runs its own sequence, so it starts again at one. It needs
        // stock that is actually in the warehouse, hence a separate host.
        await using var stocked = await new WarehouseTestHost().StartAsync(epcs, EpcStatus.InStock);
        using var stockedScope = stocked.Scope();

        var outward = await stockedScope.ServiceProvider
            .GetRequiredService<IDocumentService>()
            .CreateAsync(DocumentType.Outward, new CreateDocumentRequest { Epcs = epcs.Take(2).ToList() });

        outward.DocumentNumber.Should().Be($"OUT-{year}-000001");
    }

    [Fact]
    public async Task Concurrent_creation_never_produces_a_duplicate_number()
    {
        var epcs = Epcs(40);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        // Each request runs in its own scope, as it would behind the API.
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var scope = host.Scope();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

            return await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
            {
                Epcs = [epcs[i * 2], epcs[(i * 2) + 1]]
            });
        });

        var created = await Task.WhenAll(tasks);

        created.Select(d => d.DocumentNumber).Distinct().Should().HaveCount(20);
    }

    [Fact]
    public async Task Unregistered_epc_is_rejected_with_the_offending_value()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var act = () => documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = [.. epcs, "FEEDFACE"] });

        var error = await act.Should().ThrowAsync<WarehouseValidationException>();
        error.Which.Offending.Should().ContainSingle().Which.Should().Be("FEEDFACE");
    }

    [Fact]
    public async Task Malformed_epc_is_rejected_before_it_reaches_the_database()
    {
        await using var host = await new WarehouseTestHost().StartAsync(Epcs(1));

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var act = () => documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = ["NOTHEX!!"] });

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*hexadecimal*");
    }

    [Fact]
    public async Task Document_size_limit_is_enforced_and_configurable()
    {
        var epcs = Epcs(31);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        // The default ceiling is 30, which is a limit rather than an assumption.
        var act = () => documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs });

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*at most 30 EPCs*");
    }

    [Fact]
    public async Task Duplicate_epcs_in_a_request_collapse_to_one_line()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var document = await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
        {
            Epcs = [.. epcs, .. epcs, epcs[0].ToLowerInvariant()]
        });

        document.ExpectedArticles.Should().Be(3);
        document.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Outward_document_rejects_stock_that_is_not_in_the_warehouse()
    {
        var epcs = Epcs(3);

        // Registered, never received.
        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var act = () => documents.CreateAsync(DocumentType.Outward,
            new CreateDocumentRequest { Epcs = epcs });

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*not currently in stock*");
    }

    [Fact]
    public async Task Inward_document_rejects_stock_already_received()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs, EpcStatus.InStock);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var act = () => documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs });

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*already in stock*");
    }

    [Fact]
    public async Task A_gate_accepts_only_one_active_document()
    {
        var epcs = Epcs(4);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
        {
            Epcs = epcs.Take(2).ToList(),
            GateCode = WarehouseTestHost.GateCode
        });

        var second = await documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs.Skip(2).ToList() });

        var act = () => documents.ReleaseAsync(second.Id, WarehouseTestHost.GateCode);

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*already has an active document*");
    }

    [Fact]
    public async Task Cancelling_frees_the_gate()
    {
        var epcs = Epcs(4);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var first = await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
        {
            Epcs = epcs.Take(2).ToList(),
            GateCode = WarehouseTestHost.GateCode
        });

        var cancelled = await documents.CancelAsync(first.Id, "Truck rejected at the gate");
        cancelled.Status.Should().Be(DocumentStatus.Cancelled);
        cancelled.CancelledReason.Should().Be("Truck rejected at the gate");

        var second = await documents.CreateAsync(DocumentType.Inward,
            new CreateDocumentRequest { Epcs = epcs.Skip(2).ToList() });

        var released = await documents.ReleaseAsync(second.Id, WarehouseTestHost.GateCode);
        released.Status.Should().Be(DocumentStatus.Released);
    }

    [Fact]
    public async Task A_completed_document_cannot_be_cancelled_or_retried()
    {
        var epcs = Epcs(2);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var document = await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
        {
            Epcs = epcs,
            GateCode = WarehouseTestHost.GateCode
        });

        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        // A fresh scope, as the next HTTP request would get. The original
        // scope still tracks the pre-cycle entity and would read it stale.
        using var after = host.Scope();
        var reread = after.ServiceProvider.GetRequiredService<IDocumentService>();

        var completed = await reread.GetAsync(document.Id);
        completed!.Status.Should().Be(DocumentStatus.Completed);

        await FluentActions.Awaiting(() => reread.CancelAsync(document.Id, "too late"))
            .Should().ThrowAsync<WarehouseValidationException>();

        await FluentActions.Awaiting(() => reread.RetryAsync(document.Id))
            .Should().ThrowAsync<WarehouseValidationException>();
    }

    [Fact]
    public async Task Retry_lets_a_failed_movement_run_again()
    {
        var epcs = Epcs(5);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using var scope = host.Scope();
        var documents = Documents(host, scope);

        var document = await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
        {
            Epcs = epcs,
            GateCode = WarehouseTestHost.GateCode
        });

        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);

        // A tag is missed on the first pass.
        await host.RunCycleAsync(epcs.Take(4));

        using var afterCycle = host.Scope();
        var reread = afterCycle.ServiceProvider.GetRequiredService<IDocumentService>();

        var afterFailure = await reread.GetAsync(document.Id);
        afterFailure!.Status.Should().NotBe(DocumentStatus.Completed);

        var retried = await reread.RetryAsync(document.Id);
        retried.RetryCount.Should().Be(1);

        // The gate re-armed itself after the alarm; arming again is a no-op.
        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        using var final_ = host.Scope();
        var final = await final_.ServiceProvider.GetRequiredService<IDocumentService>()
            .GetAsync(document.Id);
        final!.Status.Should().Be(DocumentStatus.Completed);
        final.BalanceArticles.Should().Be(0);
    }
}

/// <summary>Transactional inventory behaviour (§27, §28).</summary>
public class InventoryServiceTests
{
    private static string[] Epcs(int count) =>
        Enumerable.Range(1, count).Select(i => $"E2801160{i:D8}").ToArray();

    [Fact]
    public async Task Committing_the_same_cycle_twice_is_a_no_op()
    {
        var epcs = Epcs(3);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using (var scope = host.Scope())
        {
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

            await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
            {
                Epcs = epcs,
                GateCode = WarehouseTestHost.GateCode
            });
        }

        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var (cycleId, cycleKey, documentId) = await host.WithDbAsync(async db =>
        {
            var cycle = await db.GateCycles.FirstAsync();
            return (cycle.Id, cycle.CycleId, cycle.DocumentId!.Value);
        });

        using (var scope = host.Scope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            var replay = await inventory.CommitCycleAsync(new InventoryCommitRequest
            {
                GateCycleId = cycleId,
                CycleId = cycleKey,
                DocumentId = documentId,
                MatchedEpcs = epcs
            });

            replay.Committed.Should().BeFalse();
            replay.Reason.Should().Contain("already committed");
        }

        var movements = await host.WithDbAsync(db => db.InventoryMovements.CountAsync());
        movements.Should().Be(3);
    }

    [Fact]
    public async Task Movement_ledger_records_the_status_transition()
    {
        var epcs = Epcs(2);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        using (var scope = host.Scope())
        {
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

            await documents.CreateAsync(DocumentType.Inward, new CreateDocumentRequest
            {
                Epcs = epcs,
                GateCode = WarehouseTestHost.GateCode
            });
        }

        await host.Gates.ArmAsync(WarehouseTestHost.GateCode);
        await host.RunCycleAsync(epcs);

        var movements = await host.WithDbAsync(db => db.InventoryMovements.ToListAsync());

        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(m => m.PreviousStatus == EpcStatus.Registered);
        movements.Should().OnlyContain(m => m.NewStatus == EpcStatus.InStock);
        movements.Should().OnlyContain(m => m.Direction == DocumentType.Inward);
        movements.Should().OnlyContain(m => m.Quantity == 4);
    }
}

/// <summary>EPC import validation (§44).</summary>
public class EpcImportTests
{
    [Fact]
    public async Task Import_accepts_good_rows_and_reports_bad_ones()
    {
        await using var host = await new WarehouseTestHost().StartAsync();

        using var scope = host.Scope();
        var import = scope.ServiceProvider.GetRequiredService<IEpcImportService>();

        var result = await import.ImportAsync(
        [
            new EpcImportRow { Epc = "E28011606000020000000001", ItemCode = "SKU-1", UnitQuantity = 4 },
            new EpcImportRow { Epc = "e28011606000020000000002", ItemCode = "SKU-1", UnitQuantity = 4 },
            new EpcImportRow { Epc = "E28011606000020000000001", ItemCode = "duplicate" },
            new EpcImportRow { Epc = "NOT-HEX", ItemCode = "SKU-2" },
            new EpcImportRow { Epc = "", ItemCode = "SKU-3" },
            new EpcImportRow { Epc = "E28011606000020000000003", UnitQuantity = 0 }
        ], updateExisting: false);

        result.Imported.Should().Be(2);
        result.Errors.Should().HaveCount(4);

        result.Errors.Should().Contain(e => e.Reason.Contains("Duplicate"));
        result.Errors.Should().Contain(e => e.Reason.Contains("hexadecimal"));
        result.Errors.Should().Contain(e => e.Reason.Contains("empty"));
        result.Errors.Should().Contain(e => e.Reason.Contains("at least 1"));

        var stored = await host.WithDbAsync(db => db.EpcTags.ToListAsync());

        // Lower case in the file is stored normalised.
        stored.Select(t => t.Epc).Should().BeEquivalentTo(
            "E28011606000020000000001", "E28011606000020000000002");
    }

    [Fact]
    public async Task Import_can_scale_past_the_current_catalogue_size()
    {
        await using var host = await new WarehouseTestHost().StartAsync();

        using var scope = host.Scope();
        var import = scope.ServiceProvider.GetRequiredService<IEpcImportService>();

        // The brief starts at ~400 tags but must not be capped there (§3).
        var rows = Enumerable.Range(1, 2_000)
            .Select(i => new EpcImportRow { Epc = $"E280116060000200{i:D8}", UnitQuantity = 1 })
            .ToList();

        var result = await import.ImportAsync(rows, updateExisting: false);

        result.Imported.Should().Be(2_000);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Reimporting_without_update_skips_rather_than_duplicating()
    {
        await using var host = await new WarehouseTestHost().StartAsync();

        using var scope = host.Scope();
        var import = scope.ServiceProvider.GetRequiredService<IEpcImportService>();

        var rows = new List<EpcImportRow>
        {
            new() { Epc = "E28011606000020000000001", ItemName = "Original" }
        };

        await import.ImportAsync(rows, updateExisting: false);

        rows[0].ItemName = "Changed";

        var second = await import.ImportAsync(rows, updateExisting: false);
        second.Skipped.Should().Be(1);
        second.Imported.Should().Be(0);

        var third = await import.ImportAsync(rows, updateExisting: true);
        third.Updated.Should().Be(1);

        var tag = await host.WithDbAsync(db => db.EpcTags.FirstAsync());
        tag.ItemName.Should().Be("Changed");
    }
}
