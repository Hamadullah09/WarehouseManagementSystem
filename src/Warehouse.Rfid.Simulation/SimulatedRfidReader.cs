using Microsoft.Extensions.Logging;
using Warehouse.Rfid.Abstractions;

namespace Warehouse.Rfid.Simulation;

/// <summary>Simulation controls, exposed only when simulation is enabled.</summary>
public interface ISimulatedReader
{
    string ReaderId { get; }

    string GateId { get; }

    /// <summary>Drives the configured gate input active.</summary>
    Task GpioOnAsync(CancellationToken cancellationToken = default);

    /// <summary>Drives the configured gate input inactive.</summary>
    Task GpioOffAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises a state change on an arbitrary input line.</summary>
    Task SetInputAsync(GpiPin pin, bool high, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emits tag reads. Each EPC is reported <paramref name="repeats"/> times
    /// so deduplication is genuinely exercised rather than assumed.
    /// </summary>
    Task EmitTagsAsync(
        IEnumerable<string> epcs,
        int repeats = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Simulates the reader dropping off the network.</summary>
    Task DisconnectAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>Simulates the reader coming back.</summary>
    Task ReconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Simulates an SDK-level failure.</summary>
    Task RaiseErrorAsync(string operation, string message, CancellationToken cancellationToken = default);

    /// <summary>Levels currently asserted on the output lines.</summary>
    IReadOnlyDictionary<GpoPin, bool> Outputs { get; }
}

/// <summary>
/// In-process fake reader for development and automated tests (§40).
/// </summary>
/// <remarks>
/// Deterministic: it emits exactly what it is told to, in order, with no
/// timers, randomness or background threads. That is what lets the end-to-end
/// gate tests assert on outcomes rather than race with them.
///
/// This type is registered only when <c>Rfid:AllowSimulation</c> is true, and
/// the host refuses to start with that flag set outside Development. There is
/// no configuration in which a simulated reader can move real stock in
/// production.
/// </remarks>
public sealed class SimulatedRfidReader(
    RfidReaderOptions options,
    ILogger<SimulatedRfidReader> logger) : IRfidReader, ISimulatedReader
{
    private readonly Dictionary<GpiPin, bool> _inputs = new()
    {
        [GpiPin.Gpi1] = false,
        [GpiPin.Gpi2] = false,
        [GpiPin.Gpi3] = false,
        [GpiPin.Gpi4] = false
    };

    private readonly Dictionary<GpoPin, bool> _outputs = new()
    {
        [GpoPin.Gpo1] = false,
        [GpoPin.Gpo2] = false,
        [GpoPin.Gpo3] = false,
        [GpoPin.Gpo4] = false,
        [GpoPin.WiegandData0] = false,
        [GpoPin.WiegandData1] = false
    };

    private readonly Lock _sync = new();

    private ReaderConnectionState _state = ReaderConnectionState.Disconnected;
    private bool _inventorying;
    private DateTimeOffset? _connectedAt;
    private string? _lastError;

    public string ReaderId => options.ReaderId;

    public string GateId => options.GateId;

    public ReaderConnectionState State => _state;

    public bool IsInventorying => _inventorying;

    public IReadOnlyDictionary<GpoPin, bool> Outputs
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<GpoPin, bool>(_outputs);
            }
        }
    }

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<GpiChangedEventArgs>? GpiChanged;

    public event EventHandler<ReaderStateChangedEventArgs>? StateChanged;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    // --------------------------------------------------------- IRfidReader

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ReaderConnectionState.Connected, "Simulated connect");
        _connectedAt = DateTimeOffset.UtcNow;
        _lastError = null;

        logger.LogInformation("Simulated reader {ReaderId} connected", ReaderId);

        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _inventorying = false;
        SetState(ReaderConnectionState.Disconnected, "Simulated disconnect");

        return Task.CompletedTask;
    }

    public Task<bool> StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        if (_state != ReaderConnectionState.Connected)
        {
            return Task.FromResult(false);
        }

        _inventorying = true;
        logger.LogDebug("Simulated reader {ReaderId} started inventory", ReaderId);

        return Task.FromResult(true);
    }

    public Task<bool> StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        _inventorying = false;
        logger.LogDebug("Simulated reader {ReaderId} stopped inventory", ReaderId);

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<GpiState>> ReadGpioStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<GpiState> snapshot = _inputs
                .Select(kv => new GpiState(kv.Key, kv.Value))
                .ToList();

            return Task.FromResult(snapshot);
        }
    }

    public Task<bool> SetGpioOutputAsync(
        IReadOnlyCollection<GpoCommand> outputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        lock (_sync)
        {
            foreach (var output in outputs)
            {
                _outputs[output.Pin] = output.High;
            }
        }

        logger.LogDebug(
            "Simulated reader {ReaderId} set outputs: {Outputs}",
            ReaderId, string.Join(", ", outputs));

        return Task.FromResult(true);
    }

    public Task<bool> SetAntennaPowerAsync(
        IReadOnlyDictionary<int, int> powerByAntenna,
        CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<ReaderStatus> GetReaderStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(new ReaderStatus
            {
                ReaderId = ReaderId,
                State = _state,
                IsInventorying = _inventorying,
                FirmwareVersion = "SIMULATED",
                HardwareVersion = "SIMULATED",
                TemperatureCelsius = 35.0,
                Inputs = _inputs.Select(kv => new GpiState(kv.Key, kv.Value)).ToList(),
                EnabledAntennas = options.Antennas,
                ConnectedAt = _connectedAt,
                LastSeenAt = DateTimeOffset.UtcNow,
                LastError = _lastError
            });
        }
    }

    // ------------------------------------------------------ ISimulatedReader

    public Task GpioOnAsync(CancellationToken cancellationToken = default) =>
        SetInputAsync(options.Gpio.GateSignalInput, options.Gpio.GateSignalActiveHigh, cancellationToken);

    public Task GpioOffAsync(CancellationToken cancellationToken = default) =>
        SetInputAsync(options.Gpio.GateSignalInput, !options.Gpio.GateSignalActiveHigh, cancellationToken);

    public Task SetInputAsync(GpiPin pin, bool high, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _inputs[pin] = high;
        }

        logger.LogInformation("Simulated reader {ReaderId}: {Pin} -> {State}", ReaderId, pin, high ? 1 : 0);

        GpiChanged?.Invoke(this, new GpiChangedEventArgs(ReaderId, new GpiState(pin, high), DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public Task EmitTagsAsync(
        IEnumerable<string> epcs,
        int repeats = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epcs);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 1);

        var list = epcs.ToList();

        for (var round = 0; round < repeats; round++)
        {
            foreach (var epc in list)
            {
                var tag = new TagRead
                {
                    Epc = epc,
                    Rssi = -55.0 - round,
                    RssiRaw = (-55.0 - round).ToString("0.0"),
                    Antenna = options.Antennas.FirstOrDefault(1),
                    AntennaRaw = options.Antennas.FirstOrDefault(1).ToString(),
                    Count = round + 1,
                    ObservedAt = DateTimeOffset.UtcNow
                };

                TagRead?.Invoke(this, new TagReadEventArgs(ReaderId, tag));
            }
        }

        logger.LogInformation(
            "Simulated reader {ReaderId} emitted {Distinct} EPC(s) x{Repeats}", ReaderId, list.Count, repeats);

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
    {
        _inventorying = false;
        _lastError = reason;
        SetState(ReaderConnectionState.Disconnected, reason);

        return Task.CompletedTask;
    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        _lastError = null;
        _connectedAt = DateTimeOffset.UtcNow;
        SetState(ReaderConnectionState.Connected, "Simulated reconnect");

        return Task.CompletedTask;
    }

    public Task RaiseErrorAsync(string operation, string message, CancellationToken cancellationToken = default)
    {
        _lastError = message;
        Error?.Invoke(this, new ReaderErrorEventArgs(ReaderId, operation, message, "SIM"));

        return Task.CompletedTask;
    }

    private void SetState(ReaderConnectionState next, string? reason)
    {
        var previous = _state;

        if (previous == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new ReaderStateChangedEventArgs(ReaderId, previous, next, reason));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
