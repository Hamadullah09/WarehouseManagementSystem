namespace Warehouse.Domain.Entities;

/// <summary>A physical entry/exit gate. One gate owns at most one reader at a time.</summary>
public class Gate
{
    public int Id { get; set; }

    /// <summary>Stable code used in configuration and on the wire, e.g. "GATE-01".</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Location { get; set; }

    /// <summary>Movement direction this gate handles. Null means both.</summary>
    public DocumentType? Direction { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Last observed lifecycle state; projected from the in-memory state machine.</summary>
    public GateState CurrentState { get; set; } = GateState.Idle;

    /// <summary>Document currently bound to this gate, if any.</summary>
    public int? ActiveDocumentId { get; set; }
    public Document? ActiveDocument { get; set; }

    public DateTimeOffset? StateChangedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<Reader> Readers { get; set; } = [];
    public ICollection<GateCycle> Cycles { get; set; } = [];
}

/// <summary>A configured RFID reader. Connection secrets never live here.</summary>
public class Reader
{
    public int Id { get; set; }

    /// <summary>Matches <c>RfidReaderOptions.ReaderId</c>.</summary>
    public string ReaderId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Reader network address, recorded for the dashboard.</summary>
    public string? IpAddress { get; set; }

    public int? Port { get; set; }

    public string Model { get; set; } = "Chainway U300";

    public int GateId { get; set; }
    public Gate Gate { get; set; } = null!;

    public bool IsOnline { get; set; }

    public bool IsInventorying { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    public string? FirmwareVersion { get; set; }

    public string? HardwareVersion { get; set; }

    public double? TemperatureCelsius { get; set; }

    /// <summary>Comma-separated enabled antenna ports, for display.</summary>
    public string? EnabledAntennas { get; set; }

    /// <summary>Latest GPI snapshot, e.g. "GPI1=1,GPI2=0". Display only.</summary>
    public string? GpioState { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }

    public bool IsActive { get; set; } = true;

    public byte[]? RowVersion { get; set; }
}

/// <summary>Connection and SDK-level events for one reader (§32, §37).</summary>
public class ReaderEvent
{
    public long Id { get; set; }

    public int ReaderId { get; set; }
    public Reader Reader { get; set; } = null!;

    public ReaderEventType EventType { get; set; }

    public string? Message { get; set; }

    /// <summary>SDK call that produced the event, e.g. "startInventoryTag".</summary>
    public string? SdkOperation { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Every observed digital input edge and every commanded output (§32).</summary>
public class GpioEvent
{
    public long Id { get; set; }

    public int ReaderId { get; set; }
    public Reader Reader { get; set; } = null!;

    public int GateId { get; set; }

    /// <summary>Line name as reported by the SDK: "GPI1".."GPI4", "GPO1".."GPO4".</summary>
    public string Pin { get; set; } = string.Empty;

    /// <summary>True for an input edge, false for a commanded output.</summary>
    public bool IsInput { get; set; }

    public bool High { get; set; }

    /// <summary>Cycle this edge opened or closed, when it drove one.</summary>
    public long? GateCycleId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
