namespace Warehouse.Rfid.Abstractions;

public sealed class TagReadEventArgs(string readerId, TagRead tag) : EventArgs
{
    public string ReaderId { get; } = readerId;
    public TagRead Tag { get; } = tag;
}

public sealed class GpiChangedEventArgs(string readerId, GpiState state, DateTimeOffset at) : EventArgs
{
    public string ReaderId { get; } = readerId;
    public GpiState State { get; } = state;
    public DateTimeOffset At { get; } = at;
}

public sealed class ReaderStateChangedEventArgs(
    string readerId,
    ReaderConnectionState previous,
    ReaderConnectionState current,
    string? reason = null) : EventArgs
{
    public string ReaderId { get; } = readerId;
    public ReaderConnectionState Previous { get; } = previous;
    public ReaderConnectionState Current { get; } = current;
    public string? Reason { get; } = reason;
}

public sealed class ReaderErrorEventArgs(string readerId, string operation, string message, string? code = null)
    : EventArgs
{
    public string ReaderId { get; } = readerId;

    /// <summary>SDK operation that failed, e.g. "startInventoryTag".</summary>
    public string Operation { get; } = operation;

    public string Message { get; } = message;
    public string? Code { get; } = code;
}

/// <summary>
/// Vendor-neutral contract for a UHF RFID reader with GPIO.
/// </summary>
/// <remarks>
/// Nothing above this interface may reference Chainway/RSCJA types. The U300
/// implementation lives in <c>Warehouse.Rfid.U300</c>; a deterministic
/// in-process fake lives in <c>Warehouse.Rfid.Simulation</c>. Swapping reader
/// hardware means adding one implementation here and changing configuration,
/// with no change to gate, document, inventory or alarm logic.
///
/// Implementations must be safe to call from multiple threads and must never
/// let a transport or SDK exception escape: failures surface as a false return
/// value plus an <see cref="Error"/> event, so a reader fault can never crash
/// the host.
/// </remarks>
public interface IRfidReader : IAsyncDisposable
{
    /// <summary>Stable identifier matching the Readers row in the database.</summary>
    string ReaderId { get; }

    ReaderConnectionState State { get; }

    /// <summary>True when the reader is currently running an inventory round.</summary>
    bool IsInventorying { get; }

    /// <summary>Raised for every tag observation. High frequency: handlers must not block.</summary>
    event EventHandler<TagReadEventArgs>? TagRead;

    /// <summary>
    /// Raised on a digital input edge. This is a push from the reader
    /// (vendor <c>setGPIStateCallback</c>), not a poll.
    /// </summary>
    event EventHandler<GpiChangedEventArgs>? GpiChanged;

    event EventHandler<ReaderStateChangedEventArgs>? StateChanged;

    event EventHandler<ReaderErrorEventArgs>? Error;

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<ReaderStatus> GetReaderStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> StartInventoryAsync(CancellationToken cancellationToken = default);

    Task<bool> StopInventoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GpiState>> ReadGpioStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Drives one or more output lines in a single SDK round trip.</summary>
    Task<bool> SetGpioOutputAsync(
        IReadOnlyCollection<GpoCommand> outputs,
        CancellationToken cancellationToken = default);

    /// <summary>Sets transmit power per antenna port, in dBm.</summary>
    Task<bool> SetAntennaPowerAsync(
        IReadOnlyDictionary<int, int> powerByAntenna,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the reader bound to a gate. Registered as a singleton so gate
/// services never construct or own reader lifetimes.
/// </summary>
public interface IRfidReaderRegistry
{
    IReadOnlyCollection<IRfidReader> All { get; }

    bool TryGet(string readerId, out IRfidReader reader);

    IRfidReader? ForGate(string gateId);
}
