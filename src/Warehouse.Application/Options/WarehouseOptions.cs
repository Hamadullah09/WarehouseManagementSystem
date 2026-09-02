using System.ComponentModel.DataAnnotations;

namespace Warehouse.Application.Options;

/// <summary>Gate cycle timing and policy. All values configurable (§34).</summary>
public sealed class GateOptions
{
    public const string SectionName = "Gate";

    /// <summary>
    /// Hard ceiling on how long a cycle may stay open. If the input never
    /// clears (stuck sensor, cut wire) the cycle is force-closed and validated
    /// so the gate cannot hang forever.
    /// </summary>
    [Range(1_000, 3_600_000)]
    public int CycleTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Grace period after the input clears before the EPC set is frozen.
    /// Covers reads already in flight from the reader when the signal dropped.
    /// </summary>
    [Range(0, 30_000)]
    public int DrainMs { get; set; } = 750;

    /// <summary>
    /// Minimum gap between two accepted gate-signal edges. A second edge
    /// inside this window is treated as a duplicate event, not a new cycle (§28).
    /// </summary>
    [Range(0, 60_000)]
    public int MinimumCycleIntervalMs { get; set; } = 500;

    /// <summary>Automatically re-arm the gate after a passed cycle.</summary>
    public bool AutoRearmAfterPass { get; set; } = true;

    /// <summary>
    /// Automatically re-arm after a failed cycle so the operator can push the
    /// load back through. False forces an explicit acknowledgement first.
    /// </summary>
    public bool AutoRearmAfterAlarm { get; set; }

    /// <summary>Refuse to open a cycle when the reader is not healthy (§29).</summary>
    public bool BlockCycleWhenReaderOffline { get; set; } = true;

    // Validation policy, surfaced here so it is configuration rather than code.
    public bool RequireAllExpected { get; set; } = true;

    public bool FailOnUnknownEpc { get; set; } = true;

    public bool FailOnUnexpectedEpc { get; set; } = true;

    public bool FailOnEmptyRead { get; set; } = true;

    public bool RequireHealthyReader { get; set; } = true;

    /// <summary>Zero disables the sanity ceiling on distinct EPCs per cycle.</summary>
    [Range(0, 100_000)]
    public int MaxEpcsPerCycle { get; set; }
}

/// <summary>Document creation and numbering policy.</summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// Largest number of EPC lines one document may carry. Thirty is the
    /// current operating figure; it is a limit, not an assumption (§4).
    /// </summary>
    [Range(1, 100_000)]
    public int MaxEpcsPerDocument { get; set; } = 30;

    /// <summary>Zero-padding width of the numeric part; 6 yields 000001.</summary>
    [Range(4, 12)]
    public int NumberPadding { get; set; } = 6;

    public string InwardPrefix { get; set; } = "IN";

    public string OutwardPrefix { get; set; } = "OUT";

    public string CyclePrefix { get; set; } = "GC";

    public string AlarmPrefix { get; set; } = "AL";

    /// <summary>Cap on retries of a failed document before a supervisor must intervene.</summary>
    [Range(0, 100)]
    public int MaxRetries { get; set; } = 5;
}

/// <summary>Alarm behaviour.</summary>
public sealed class AlarmOptions
{
    public const string SectionName = "Alarms";

    /// <summary>Drive the configured GPO beacon when an alarm is raised.</summary>
    public bool DriveGpioOutput { get; set; } = true;

    /// <summary>Require a supervisor role to resolve alarms.</summary>
    public bool RequireSupervisorToResolve { get; set; } = true;
}

/// <summary>Authentication policy. Values a site may reasonably differ on (§34).</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Shortest password accepted when a user changes their own.
    /// </summary>
    /// <remarks>
    /// Twelve is the default because a warehouse account that can move stock
    /// deserves it. Sites running on a closed network behind a door sometimes
    /// choose shorter; that is their call to make in configuration, not one to
    /// bake into the build.
    /// </remarks>
    [Range(4, 128)]
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>Failed attempts before the account is locked.</summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a lockout lasts, in minutes.</summary>
    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;
}
