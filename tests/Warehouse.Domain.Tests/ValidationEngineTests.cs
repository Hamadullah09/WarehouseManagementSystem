using FluentAssertions;
using Warehouse.Domain;
using Warehouse.Domain.Validation;
using Xunit;

namespace Warehouse.Domain.Tests;

/// <summary>
/// The failure matrix from the brief (§39), expressed directly.
/// </summary>
/// <remarks>
/// The engine is pure, so every case here is exact rather than probabilistic:
/// no database, no clock, no reader. If one of these fails, the gate's notion
/// of a valid movement has changed, which is precisely what should be hard to
/// do by accident.
/// </remarks>
public class ValidationEngineTests
{
    private readonly ValidationEngine _engine = new();

    private static ValidationInput Input(
        IEnumerable<string> expected,
        IEnumerable<string> detected,
        IEnumerable<string>? known = null,
        IEnumerable<string>? blocked = null,
        bool readerHealthy = true,
        ValidationPolicy? policy = null)
    {
        var detectedList = detected.ToList();

        return new ValidationInput
        {
            DocumentType = DocumentType.Inward,
            ExpectedEpcs = expected.ToList(),
            DetectedEpcs = detectedList,
            // By default every detected EPC is a known one, so tests opt in to
            // the unknown case rather than tripping over it.
            KnownEpcs = (known ?? detectedList).ToHashSet(Epc.Comparer),
            BlockedEpcs = (blocked ?? []).ToHashSet(Epc.Comparer),
            ReaderHealthy = readerHealthy,
            Policy = policy ?? ValidationPolicy.Strict
        };
    }

    private static string[] Epcs(int count, int start = 1) =>
        Enumerable.Range(start, count).Select(i => $"E280{i:D8}").ToArray();

    [Fact]
    public void All_expected_epcs_detected_passes()
    {
        var epcs = Epcs(30);

        var result = _engine.Validate(Input(epcs, epcs));

        result.Outcome.Should().Be(ValidationOutcome.Pass);
        result.Matched.Should().HaveCount(30);
        result.Missing.Should().BeEmpty();
        result.Unknown.Should().BeEmpty();
        result.Unexpected.Should().BeEmpty();
        result.Alarms.Should().BeEmpty();
        result.Summary.Should().StartWith("PASS");
    }

    [Fact]
    public void Missing_one_epc_fails_and_names_it()
    {
        var expected = Epcs(30);
        var detected = expected.Take(29).ToArray();

        var result = _engine.Validate(Input(expected, detected));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.DetectedCount.Should().Be(29);
        result.ExpectedCount.Should().Be(30);
        result.Missing.Should().ContainSingle().Which.Should().Be(expected[29]);
        result.Alarms.Should().Contain(AlarmType.MissingEpc);
    }

    [Fact]
    public void Unknown_epc_fails_and_is_not_reported_as_unexpected()
    {
        var expected = Epcs(3);
        var detected = expected.Append("DEADBEEF").ToArray();

        // The stray EPC is deliberately absent from the known set.
        var result = _engine.Validate(Input(expected, detected, known: expected));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Unknown.Should().ContainSingle().Which.Should().Be("DEADBEEF");
        result.Unexpected.Should().BeEmpty();
        result.Alarms.Should().Contain(AlarmType.UnknownEpc);
    }

    [Fact]
    public void Known_but_unexpected_epc_fails_and_is_not_reported_as_unknown()
    {
        var expected = Epcs(3);
        var stray = Epcs(1, start: 99).Single();
        var detected = expected.Append(stray).ToArray();

        // The stray IS in the catalogue; it simply is not on this document.
        var result = _engine.Validate(Input(expected, detected, known: detected));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Unexpected.Should().ContainSingle().Which.Should().Be(stray);
        result.Unknown.Should().BeEmpty();
        result.Alarms.Should().Contain(AlarmType.UnexpectedEpc);
    }

    [Fact]
    public void Unknown_and_unexpected_are_distinguished_in_the_same_cycle()
    {
        var expected = Epcs(2);
        var knownStray = Epcs(1, start: 50).Single();
        var detected = expected.Concat([knownStray, "CAFEBABE"]).ToArray();

        var result = _engine.Validate(
            Input(expected, detected, known: expected.Append(knownStray)));

        result.Unknown.Should().ContainSingle().Which.Should().Be("CAFEBABE");
        result.Unexpected.Should().ContainSingle().Which.Should().Be(knownStray);
        result.Alarms.Should().Contain(AlarmType.UnknownEpc);
        result.Alarms.Should().Contain(AlarmType.UnexpectedEpc);

        // Severity order: unknown outranks unexpected on the display banner.
        result.PrimaryAlarm.Should().Be(AlarmType.UnknownEpc);
    }

    [Fact]
    public void Zero_epcs_detected_raises_the_no_epc_alarm()
    {
        var result = _engine.Validate(Input(Epcs(5), []));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.DetectedCount.Should().Be(0);
        result.Alarms.Should().Contain(AlarmType.NoEpc);

        // An empty read is reported as "nothing was seen", not as 5 separate
        // missing tags, because the likely cause is an untagged item.
        result.Alarms.Should().NotContain(AlarmType.MissingEpc);
    }

    [Fact]
    public void Duplicate_reads_of_one_epc_count_once()
    {
        var expected = Epcs(3);

        // The caller deduplicates, but the engine must be idempotent anyway.
        var detected = expected.Concat(expected).Concat(expected).ToArray();

        var result = _engine.Validate(Input(expected, detected));

        result.Outcome.Should().Be(ValidationOutcome.Pass);
        result.DetectedCount.Should().Be(3);
        result.Matched.Should().HaveCount(3);
    }

    [Fact]
    public void Extra_expected_epc_beyond_the_document_is_unexpected()
    {
        var expected = Epcs(30);
        var detected = Epcs(31);

        var result = _engine.Validate(Input(expected, detected, known: detected));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.DetectedCount.Should().Be(31);
        result.Unexpected.Should().ContainSingle();
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void Retired_or_blocked_tag_is_unexpected_even_when_on_the_document()
    {
        var expected = Epcs(3);
        var blocked = expected[1];

        var result = _engine.Validate(Input(expected, expected, blocked: [blocked]));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Unexpected.Should().ContainSingle().Which.Should().Be(blocked);
        result.Matched.Should().NotContain(blocked);

        // It was on the document but never seen as valid, so it is also outstanding.
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void Unhealthy_reader_fails_an_otherwise_perfect_cycle()
    {
        var epcs = Epcs(10);

        var result = _engine.Validate(Input(epcs, epcs, readerHealthy: false));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Alarms.Should().Contain(AlarmType.ReaderError);
        result.Missing.Should().BeEmpty();
        result.Summary.Should().Contain("reader unhealthy");
    }

    [Fact]
    public void Relaxed_policy_allows_a_partial_cycle_to_pass()
    {
        var expected = Epcs(30);
        var detected = expected.Take(20).ToArray();

        var policy = ValidationPolicy.Strict with { RequireAllExpected = false };

        var result = _engine.Validate(Input(expected, detected, policy: policy));

        result.Outcome.Should().Be(ValidationOutcome.Pass);
        result.Matched.Should().HaveCount(20);

        // Still reported, so the document knows what remains outstanding.
        result.Missing.Should().HaveCount(10);
    }

    [Fact]
    public void Cycle_ceiling_flags_a_read_far_larger_than_the_document()
    {
        var expected = Epcs(3);
        var detected = Epcs(50);

        var policy = ValidationPolicy.Strict with { MaxEpcsPerCycle = 10 };

        var result = _engine.Validate(Input(expected, detected, known: detected, policy: policy));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Alarms.Should().Contain(AlarmType.DocumentMismatch);
    }

    [Fact]
    public void Empty_document_with_a_read_reports_everything_as_unexpected()
    {
        var detected = Epcs(2);

        var result = _engine.Validate(Input([], detected, known: detected));

        result.Outcome.Should().Be(ValidationOutcome.Fail);
        result.Unexpected.Should().HaveCount(2);
        result.Alarms.Should().Contain(AlarmType.UnexpectedEpc);
    }

    [Fact]
    public void Result_is_deterministic_and_ordered()
    {
        var expected = new[] { "E2800003", "E2800001", "E2800002" };
        var detected = new[] { "E2800002", "E2800001" };

        var first = _engine.Validate(Input(expected, detected));
        var second = _engine.Validate(Input(expected, detected));

        first.Should().BeEquivalentTo(second);
        first.Matched.Should().BeInAscendingOrder();
        first.Missing.Should().Equal("E2800003");
    }
}
