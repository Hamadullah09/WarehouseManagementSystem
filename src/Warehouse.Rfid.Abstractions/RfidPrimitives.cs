namespace Warehouse.Rfid.Abstractions;

/// <summary>
/// Connection lifecycle of a physical reader, mirroring the vendor
/// <c>ConnectionState</c> callback plus the states the adapter itself owns.
/// </summary>
public enum ReaderConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Faulted = 4
}

/// <summary>
/// Digital input lines exposed by the U300 GPIO terminal. The vendor SDK
/// identifies these by the literal strings "GPI1".."GPI4"
/// (<c>com.rscja.deviceapi.entity.GPIStateEntity</c>).
/// </summary>
public enum GpiPin
{
    Gpi1 = 1,
    Gpi2 = 2,
    Gpi3 = 3,
    Gpi4 = 4
}

/// <summary>
/// Digital output lines. "GPO1".."GPO4" are the optocoupler open-collector
/// outputs; the two Wiegand data lines are addressable through the same
/// <c>outputOnAndOff(List&lt;GPOEntity&gt;)</c> call
/// (<c>com.rscja.deviceapi.entity.GPOEntity</c>).
/// </summary>
public enum GpoPin
{
    Gpo1 = 1,
    Gpo2 = 2,
    Gpo3 = 3,
    Gpo4 = 4,
    WiegandData0 = 10,
    WiegandData1 = 11
}

/// <summary>
/// How the reader decides when to run an inventory. Mirrors the three modes
/// offered by the U300 transmission service (see integration guide §4.3.3.2).
/// </summary>
public enum InventoryTriggerMode
{
    /// <summary>Host issues start/stop. The adapter reacts to GPI edges itself.</summary>
    Command = 0,

    /// <summary>Reader firmware starts/stops on the GPI edge. Hardware-timed.</summary>
    Trigger = 1,

    /// <summary>Reader inventories continuously from power-on.</summary>
    Automatic = 2
}

/// <summary>A single tag observation as reported by the reader.</summary>
/// <remarks>
/// Field-for-field this is <c>com.rscja.deviceapi.entity.UHFTAGInfo</c>. The
/// vendor returns RSSI and antenna as strings; they are parsed here but the
/// raw text is preserved so nothing is lost when the reader reports a value
/// the parser does not expect.
/// </remarks>
public sealed record TagRead
{
    public required string Epc { get; init; }
    public string? Tid { get; init; }
    public string? User { get; init; }
    public string? Pc { get; init; }

    /// <summary>Signal strength in dBm when parseable, else null.</summary>
    public double? Rssi { get; init; }

    public string? RssiRaw { get; init; }

    /// <summary>Antenna port that saw the tag, 1-based, when parseable.</summary>
    public int? Antenna { get; init; }

    public string? AntennaRaw { get; init; }

    /// <summary>Read count reported by the reader for this tag.</summary>
    public int Count { get; init; }

    /// <summary>When the adapter observed the read (server clock, UTC).</summary>
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>Reader-supplied timestamp, when present.</summary>
    public long? ReaderTimestamp { get; init; }
}

/// <summary>State of one digital input line.</summary>
public readonly record struct GpiState(GpiPin Pin, bool High)
{
    public override string ToString() => $"{Pin}={(High ? 1 : 0)}";
}

/// <summary>A requested level for one digital output line.</summary>
public readonly record struct GpoCommand(GpoPin Pin, bool High)
{
    public override string ToString() => $"{Pin}={(High ? 1 : 0)}";
}

/// <summary>Point-in-time health snapshot used by the UI and the gate guard rails.</summary>
public sealed record ReaderStatus
{
    public required string ReaderId { get; init; }
    public required ReaderConnectionState State { get; init; }
    public bool IsInventorying { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? HardwareVersion { get; init; }
    public double? TemperatureCelsius { get; init; }
    public IReadOnlyList<GpiState> Inputs { get; init; } = Array.Empty<GpiState>();
    public IReadOnlyList<int> EnabledAntennas { get; init; } = Array.Empty<int>();
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? LastError { get; init; }

    /// <summary>
    /// True only when the reader is connected and has not reported a fault.
    /// A gate cycle must never be allowed to complete unless this holds.
    /// </summary>
    public bool IsHealthy => State == ReaderConnectionState.Connected && LastError is null;
}
