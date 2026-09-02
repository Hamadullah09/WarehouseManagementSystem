using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Documents;
using Warehouse.Application.Gates;
using Warehouse.Domain;
using Xunit;

namespace Warehouse.Application.Tests;

/// <summary>
/// The path used by the on-reader app: the device reads, then submits the
/// whole session for the server to rule on.
/// </summary>
public class DeviceSessionTests
{
    private const string DeviceId = "U300-TEST";

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

    private static async Task<DeviceSessionResult> SubmitAsync(
        WarehouseTestHost host,
        int documentId,
        IReadOnlyList<string> epcs,
        string? sessionKey = null,
        bool readerHealthy = true)
    {
        using var scope = host.Scope();
        var sessions = scope.ServiceProvider.GetRequiredService<IDeviceSessionService>();

        return await sessions.SubmitAsync(documentId, new DeviceSessionRequest
        {
            GateCode = WarehouseTestHost.GateCode,
            DeviceId = DeviceId,
            DetectedEpcs = epcs,
            RawReadCount = epcs.Count * 4,
            ReaderHealthy = readerHealthy,
            SessionKey = sessionKey ?? Guid.NewGuid().ToString()
        });
    }

    [Fact]
    public async Task Complete_session_passes_and_commits()
    {
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var result = await SubmitAsync(host, document.Id, epcs);

        result.Passed.Should().BeTrue();
        result.MovedArticles.Should().Be(30);
        result.DocumentStatus.Should().Be(DocumentStatus.Completed);
        result.BalanceArticles.Should().Be(0);
        result.CycleId.Should().MatchRegex(@"^GC-\d{4}-\d{6}$");
    }

    [Fact]
    public async Task Short_read_fails_and_names_the_missing_rolls()
    {
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var result = await SubmitAsync(host, document.Id, epcs.Take(28).ToArray());

        result.Passed.Should().BeFalse();
        result.Missing.Should().HaveCount(2);
        result.MovedArticles.Should().Be(0);
        result.Alarms.Should().Contain(nameof(AlarmType.MissingEpc));
    }

    [Fact]
    public async Task Tag_from_another_document_is_reported_as_unexpected_not_unknown()
    {
        // Both sets are registered in the warehouse; only one is on the
        // document. The distinction is what tells the operator whether they
        // have the wrong pallet or a security problem.
        var onDocument = Epcs(10);
        var elsewhere = Epcs(5, 900);

        await using var host = await new WarehouseTestHost().StartAsync(onDocument.Concat(elsewhere));

        var document = await CreateDocumentAsync(host, DocumentType.Inward, onDocument);
        var result = await SubmitAsync(host, document.Id, onDocument.Concat(elsewhere.Take(1)).ToArray());

        result.Passed.Should().BeFalse();
        result.Unexpected.Should().ContainSingle().Which.Should().Be(elsewhere[0]);
        result.Unknown.Should().BeEmpty();
        result.Alarms.Should().Contain(nameof(AlarmType.UnexpectedEpc));
    }

    [Fact]
    public async Task Tag_not_in_the_catalogue_is_reported_as_unknown()
    {
        var epcs = Epcs(10);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var result = await SubmitAsync(host, document.Id, epcs.Append("DEADBEEFCAFE1234").ToArray());

        result.Passed.Should().BeFalse();
        result.Unknown.Should().ContainSingle().Which.Should().Be("DEADBEEFCAFE1234");
        result.Alarms.Should().Contain(nameof(AlarmType.UnknownEpc));
    }

    [Fact]
    public async Task Empty_session_raises_the_no_tag_alarm()
    {
        var epcs = Epcs(10);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var result = await SubmitAsync(host, document.Id, Array.Empty<string>());

        result.Passed.Should().BeFalse();
        result.Alarms.Should().Contain(nameof(AlarmType.NoEpc));
        result.MovedArticles.Should().Be(0);
    }

    [Fact]
    public async Task Unhealthy_reader_cannot_produce_a_pass()
    {
        var epcs = Epcs(10);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var result = await SubmitAsync(host, document.Id, epcs, readerHealthy: false);

        result.Passed.Should().BeFalse();
        result.Alarms.Should().Contain(nameof(AlarmType.ReaderError));
        result.MovedArticles.Should().Be(0);
    }

    [Fact]
    public async Task Retrying_a_submission_returns_the_original_verdict_and_moves_nothing()
    {
        // The case this exists for: the device commits, the network drops
        // before the response arrives, and the device retries. By then the
        // document is Completed, and answering with an error would send the
        // operator to rescan a load that is already in.
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var key = Guid.NewGuid().ToString();

        var first = await SubmitAsync(host, document.Id, epcs, key);
        first.Passed.Should().BeTrue();
        first.MovedArticles.Should().Be(30);
        first.WasReplay.Should().BeFalse();

        var retry = await SubmitAsync(host, document.Id, epcs, key);
        retry.WasReplay.Should().BeTrue();
        retry.Passed.Should().BeTrue();
        retry.MovedArticles.Should().Be(0);
        retry.CycleId.Should().Be(first.CycleId);
    }

    [Fact]
    public async Task A_fresh_submission_against_a_completed_document_is_refused()
    {
        var epcs = Epcs(30);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        await SubmitAsync(host, document.Id, epcs);

        var act = () => SubmitAsync(host, document.Id, epcs);

        await act.Should().ThrowAsync<WarehouseValidationException>()
            .WithMessage("*Completed*");
    }

    [Fact]
    public async Task Device_reads_are_deduplicated_server_side()
    {
        // The app already de-duplicates, but the server must not depend on it.
        var epcs = Epcs(10);

        await using var host = await new WarehouseTestHost().StartAsync(epcs);

        var document = await CreateDocumentAsync(host, DocumentType.Inward, epcs);
        var repeated = epcs.Concat(epcs).Concat(epcs).ToArray();

        var result = await SubmitAsync(host, document.Id, repeated);

        result.DetectedCount.Should().Be(10);
        result.MovedArticles.Should().Be(10);
        result.Passed.Should().BeTrue();
    }
}
