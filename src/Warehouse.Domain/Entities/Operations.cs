namespace Warehouse.Domain.Entities;

/// <summary>An operational alarm. Every alarm is auditable and must be resolved by a person (§18).</summary>
public class Alarm
{
    public long Id { get; set; }

    public string AlarmId { get; set; } = string.Empty;

    public int? GateId { get; set; }
    public Gate? Gate { get; set; }

    public int? DocumentId { get; set; }
    public Document? Document { get; set; }

    public long? GateCycleId { get; set; }
    public GateCycle? GateCycle { get; set; }

    public int? ReaderId { get; set; }

    public AlarmType AlarmType { get; set; }

    public AlarmStatus Status { get; set; } = AlarmStatus.Active;

    public string Message { get; set; } = string.Empty;

    /// <summary>The offending EPC, when the alarm is about one tag.</summary>
    public string? Epc { get; set; }

    /// <summary>All offending EPCs as a newline-separated list, when several apply.</summary>
    public string? EpcList { get; set; }

    public DateTimeOffset RaisedAt { get; set; }

    public int? AcknowledgedByUserId { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public int? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? ResolutionNotes { get; set; }

    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Append-only audit trail: who, what, when, which document/gate/EPC/cycle,
/// and the state transition (§32).
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public AuditAction Action { get; set; }

    public int? UserId { get; set; }

    /// <summary>Captured at write time so the trail survives a user rename.</summary>
    public string? UserName { get; set; }

    public int? GateId { get; set; }

    public int? DocumentId { get; set; }

    public string? DocumentNumber { get; set; }

    public long? GateCycleId { get; set; }

    public string? CycleId { get; set; }

    public int? ReaderId { get; set; }

    public string? Epc { get; set; }

    public string? PreviousState { get; set; }

    public string? NewState { get; set; }

    public string? Result { get; set; }

    /// <summary>Free-form detail. Must never contain credentials or tokens.</summary>
    public string? Details { get; set; }

    public string? CorrelationId { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Runtime-tunable setting. Anything an operator may need to change lives here, not in source (§44).</summary>
public class SystemSetting
{
    public int Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string? Description { get; set; }

    public string? Category { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }
}

/// <summary>
/// Per-type, per-year counter backing document and cycle numbering.
/// </summary>
/// <remarks>
/// Allocation runs inside the caller's transaction with an UPDLOCK/ROWLOCK
/// hint, so two concurrent requests serialise rather than colliding on the
/// unique index (§5).
/// </remarks>
public class NumberSequence
{
    public int Id { get; set; }

    /// <summary>Sequence discriminator, e.g. "IN", "OUT", "GC".</summary>
    public string Prefix { get; set; } = string.Empty;

    public int Year { get; set; }

    public long LastValue { get; set; }

    public byte[]? RowVersion { get; set; }
}
