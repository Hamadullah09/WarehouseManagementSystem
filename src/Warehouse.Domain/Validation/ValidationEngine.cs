namespace Warehouse.Domain.Validation;

public interface IValidationEngine
{
    ValidationResult Validate(ValidationInput input);
}

/// <summary>
/// Decides whether one gate cycle represents a legitimate movement (§26, §43).
/// </summary>
/// <remarks>
/// Pure and deterministic: same input, same verdict, no I/O, no clock, no
/// logging. That is what makes the failure matrix in the test suite meaningful
/// and lets the same rules run in a what-if replay against historical cycles.
///
/// A pass is never granted just because tags were seen. It requires, together:
/// every expected EPC present, nothing unknown, nothing unexpected, a non-empty
/// read, and a reader that stayed healthy throughout.
/// </remarks>
public sealed class ValidationEngine : IValidationEngine
{
    public ValidationResult Validate(ValidationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var policy = input.Policy;

        var expected = new HashSet<string>(input.ExpectedEpcs, Epc.Comparer);
        var detected = new HashSet<string>(input.DetectedEpcs, Epc.Comparer);

        // Partition the detected set. Order matters only for readability; each
        // EPC lands in exactly one bucket.
        var matched = new List<string>();
        var unknown = new List<string>();
        var unexpected = new List<string>();

        foreach (var epc in detected)
        {
            if (!input.KnownEpcs.Contains(epc))
            {
                unknown.Add(epc);
            }
            else if (input.BlockedEpcs.Contains(epc))
            {
                // Real tag, but retired or blocked: it must not move stock.
                unexpected.Add(epc);
            }
            else if (expected.Contains(epc))
            {
                matched.Add(epc);
            }
            else
            {
                unexpected.Add(epc);
            }
        }

        var missing = expected.Where(e => !detected.Contains(e)).ToList();

        matched.Sort(Epc.Comparer);
        unknown.Sort(Epc.Comparer);
        unexpected.Sort(Epc.Comparer);
        missing.Sort(Epc.Comparer);

        var alarms = new List<AlarmType>();

        // Ordered by severity: an unknown tag is the most serious thing a gate
        // can see, because nothing in the warehouse accounts for it.
        if (policy.RequireHealthyReader && !input.ReaderHealthy)
        {
            alarms.Add(AlarmType.ReaderError);
        }

        if (detected.Count == 0 && policy.FailOnEmpty)
        {
            alarms.Add(AlarmType.NoEpc);
        }

        if (unknown.Count > 0 && policy.FailOnUnknown)
        {
            alarms.Add(AlarmType.UnknownEpc);
        }

        if (unexpected.Count > 0 && policy.FailOnUnexpected)
        {
            alarms.Add(AlarmType.UnexpectedEpc);
        }

        if (missing.Count > 0 && policy.RequireAllExpected && detected.Count > 0)
        {
            alarms.Add(AlarmType.MissingEpc);
        }

        if (policy.MaxEpcsPerCycle > 0 && detected.Count > policy.MaxEpcsPerCycle)
        {
            alarms.Add(AlarmType.DocumentMismatch);
        }

        alarms.Sort();

        var outcome = alarms.Count == 0 ? ValidationOutcome.Pass : ValidationOutcome.Fail;

        return new ValidationResult
        {
            Outcome = outcome,
            Matched = matched,
            Missing = missing,
            Unknown = unknown,
            Unexpected = unexpected,
            Alarms = alarms,
            ExpectedCount = expected.Count,
            DetectedCount = detected.Count,
            Summary = BuildSummary(outcome, expected.Count, detected.Count, unknown.Count, unexpected.Count, missing.Count, input.ReaderHealthy)
        };
    }

    private static string BuildSummary(
        ValidationOutcome outcome,
        int expected,
        int detected,
        int unknown,
        int unexpected,
        int missing,
        bool readerHealthy)
    {
        var verdict = outcome == ValidationOutcome.Pass ? "PASS" : "FAIL";
        var reader = readerHealthy ? string.Empty : ", reader unhealthy";

        return $"{verdict}: expected {expected}, detected {detected}, "
             + $"unknown {unknown}, unexpected {unexpected}, missing {missing}{reader}";
    }
}
