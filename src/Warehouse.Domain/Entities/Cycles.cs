namespace Warehouse.Domain.Entities;

/// <summary>
/// One gate pass: everything between the input going active and the verdict
/// (§10, §28). The unit of idempotency for inventory movement.
/// </summary>
public class GateCycle
{
    public long Id { get; set; }

    /// <summary>Human-readable id, e.g. "GC-2026-000001". Unique.</summary>
    public string CycleId { get; set; } = string.Empty;

    /// <summary>
    /// Idempotency key derived from the physical trigger (gate + input edge
    /// timestamp). A replayed edge maps to the same key and is rejected rather
    /// than producing a second movement.
    /// </summary>
    public string TriggerKey { get; set; } = string.Empty;

    public int GateId { get; set; }
    public Gate Gate { get; set; } = null!;

    public int ReaderId { get; set; }
    public Reader Reader { get; set; } = null!;

    public int? DocumentId { get; set; }
    public Document? Document { get; set; }

    public GateCycleStatus Status { get; set; } = GateCycleStatus.Running;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Distinct EPCs seen, after deduplication.</summary>
    public int DetectedEpcCount { get; set; }

    /// <summary>Raw reads received before deduplication. Useful for tuning antenna power.</summary>
    public int RawReadCount { get; set; }

    public int ExpectedEpcCount { get; set; }

    public int UnknownEpcCount { get; set; }

    public int UnexpectedEpcCount { get; set; }

    public int MissingEpcCount { get; set; }

    public ValidationOutcome? ValidationResult { get; set; }

    /// <summary>Short human-readable verdict for the dashboard.</summary>
    public string? ValidationSummary { get; set; }

    /// <summary>False if the reader reported a fault at any point during the cycle.</summary>
    public bool ReaderHealthy { get; set; } = true;

    /// <summary>Set once the inventory transaction has committed. Blocks re-commit.</summary>
    public bool InventoryCommitted { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<GateCycleEpc> Epcs { get; set; } = [];
    public ICollection<Alarm> Alarms { get; set; } = [];
}

/// <summary>How a single EPC observed in a cycle classified against the document.</summary>
public enum EpcClassification
{
    /// <summary>Known and expected by the active document.</summary>
    Expected = 0,

    /// <summary>Not present in the EPC catalogue at all (§13).</summary>
    Unknown = 1,

    /// <summary>Known to the warehouse but not on this document (§14).</summary>
    Unexpected = 2,

    /// <summary>Expected but never seen. Recorded with no read data (§15).</summary>
    Missing = 3
}

/// <summary>One distinct EPC within a cycle, after deduplication (§12).</summary>
public class GateCycleEpc
{
    public long Id { get; set; }

    public long GateCycleId { get; set; }
    public GateCycle GateCycle { get; set; } = null!;

    public string Epc { get; set; } = string.Empty;

    /// <summary>Null when the EPC is unknown to the warehouse.</summary>
    public long? EpcTagId { get; set; }

    public EpcClassification Classification { get; set; }

    /// <summary>Times the reader reported this EPC during the cycle.</summary>
    public int ReadCount { get; set; }

    public double? PeakRssi { get; set; }

    public int? Antenna { get; set; }

    public DateTimeOffset? FirstSeenAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}
