namespace Warehouse.Domain.Validation;

/// <summary>
/// Configurable rules for what counts as a valid gate pass (§11, §44).
/// </summary>
/// <remarks>
/// Defaults implement the brief's strict reading: every expected EPC must be
/// present, nothing else may be, and an empty read is an alarm. Sites that run
/// multi-pass loading can relax <see cref="RequireAllExpected"/> so a partial
/// cycle leaves the document in progress instead of failing it.
/// </remarks>
public sealed record ValidationPolicy
{
    /// <summary>Missing EPCs fail the cycle. Default true (§15).</summary>
    public bool RequireAllExpected { get; init; } = true;

    /// <summary>An EPC absent from the catalogue fails the cycle. Default true (§13).</summary>
    public bool FailOnUnknown { get; init; } = true;

    /// <summary>A known EPC not on the document fails the cycle. Default true (§14).</summary>
    public bool FailOnUnexpected { get; init; } = true;

    /// <summary>A cycle that read nothing fails. Default true (§16, §17).</summary>
    public bool FailOnEmpty { get; init; } = true;

    /// <summary>
    /// Reject the cycle if the reader reported any fault while it ran.
    /// Default true: a cycle can only pass on evidence from a healthy reader (§43).
    /// </summary>
    public bool RequireHealthyReader { get; init; } = true;

    /// <summary>
    /// Upper bound on distinct EPCs accepted in one cycle. Zero disables the
    /// check. A read far above the document size usually means the antenna is
    /// seeing an adjacent aisle rather than the gate.
    /// </summary>
    public int MaxEpcsPerCycle { get; init; }

    public static ValidationPolicy Strict { get; } = new();
}

/// <summary>Everything the engine needs. Deliberately free of EF, hardware and UI types.</summary>
public sealed record ValidationInput
{
    public required DocumentType DocumentType { get; init; }

    /// <summary>EPCs the document still expects. Normalised.</summary>
    public required IReadOnlyCollection<string> ExpectedEpcs { get; init; }

    /// <summary>Distinct EPCs observed this cycle, already deduplicated. Normalised.</summary>
    public required IReadOnlyCollection<string> DetectedEpcs { get; init; }

    /// <summary>
    /// Subset of <see cref="DetectedEpcs"/> that exists in the catalogue and is
    /// usable. The caller resolves this with one batched query so the engine
    /// stays free of I/O.
    /// </summary>
    public required IReadOnlySet<string> KnownEpcs { get; init; }

    /// <summary>
    /// Detected EPCs that exist but are retired or blocked. Treated as
    /// unexpected: the tag is real, but it has no business crossing the gate.
    /// </summary>
    public IReadOnlySet<string> BlockedEpcs { get; init; } = new HashSet<string>(Epc.Comparer);

    /// <summary>False if the reader faulted at any point during the cycle.</summary>
    public bool ReaderHealthy { get; init; } = true;

    public ValidationPolicy Policy { get; init; } = ValidationPolicy.Strict;
}

/// <summary>Verdict for one gate cycle.</summary>
public sealed record ValidationResult
{
    public required ValidationOutcome Outcome { get; init; }

    /// <summary>Expected EPCs that were detected. These are the ones to commit.</summary>
    public required IReadOnlyList<string> Matched { get; init; }

    /// <summary>Expected but not detected (§15).</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>Detected but absent from the catalogue (§13).</summary>
    public required IReadOnlyList<string> Unknown { get; init; }

    /// <summary>Detected and known, but not on this document (§14).</summary>
    public required IReadOnlyList<string> Unexpected { get; init; }

    /// <summary>Alarms to raise, most severe first. Empty on a pass.</summary>
    public required IReadOnlyList<AlarmType> Alarms { get; init; }

    /// <summary>One-line verdict for the dashboard and audit trail.</summary>
    public required string Summary { get; init; }

    public int ExpectedCount { get; init; }

    public int DetectedCount { get; init; }

    public bool IsPass => Outcome == ValidationOutcome.Pass;

    /// <summary>Most severe alarm, or null on a pass. Drives the gate display banner.</summary>
    public AlarmType? PrimaryAlarm => Alarms.Count > 0 ? Alarms[0] : null;
}
