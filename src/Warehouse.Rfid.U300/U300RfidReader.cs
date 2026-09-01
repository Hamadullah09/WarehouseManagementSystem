using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Warehouse.Rfid.Abstractions;
using Warehouse.Rfid.U300.Bridge;

namespace Warehouse.Rfid.U300;

/// <summary>
/// Chainway U300 driver (§24).
/// </summary>
/// <remarks>
/// The U300 runs Android 11 and Chainway ships a Java-only SDK, so this
/// adapter does not talk to the reader directly. It talks to the U300 bridge,
/// a small Java process that uses the vendor's own
/// <c>RFIDWithUHFNetworkA4</c> / <c>RFIDWithUHFSerialPortA4</c> classes against
/// the reader's slave-mode service on TCP 9160. Every reader operation this
/// class exposes maps to a documented SDK call:
///
///   ConnectAsync         -> init(ip, port) / init(comPort)
///   DisconnectAsync      -> free()
///   StartInventoryAsync  -> startInventoryTag()
///   StopInventoryAsync   -> stopInventory()
///   ReadGpioStateAsync   -> inputStatus()
///   SetGpioOutputAsync   -> outputOnAndOff(List&lt;GPOEntity&gt;)
///   SetAntennaPowerAsync -> setAntennaPower(...)
///   TagRead event        <- setInventoryCallback(IUHFInventoryCallback)
///   GpiChanged event     <- setGPIStateCallback(IGPIStateCallback)
///   StateChanged event   <- setConnectionStateCallback(ConnectionStateCallback)
///
/// Nothing here invents protocol. Where the SDK offers no equivalent the
/// method reports failure rather than improvising.
///
/// The class never throws out of an operation: transport and SDK faults become
/// a false return plus an <see cref="Error"/> event, so a reader problem
/// degrades the gate to "offline" instead of taking down the host (§37).
/// </remarks>
public sealed class U300RfidReader : IRfidReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RfidReaderOptions _options;
    private readonly ILogger<U300RfidReader> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool> _connectedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpClient? _client;
    private StreamWriter? _writer;
    private Task? _supervisor;

    private volatile ReaderConnectionState _state = ReaderConnectionState.Disconnected;
    private long _lastHeartbeatTicks;
    private volatile bool _inventorying;
    private volatile string? _lastError;
    private DateTimeOffset? _connectedAt;
    private ReaderStatus? _lastStatus;

    public U300RfidReader(RfidReaderOptions options, ILogger<U300RfidReader> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        ReaderId = options.ReaderId;
    }

    public string ReaderId { get; }

    public ReaderConnectionState State => _state;

    public bool IsInventorying => _inventorying;

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<GpiChangedEventArgs>? GpiChanged;

    public event EventHandler<ReaderStateChangedEventArgs>? StateChanged;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    // ------------------------------------------------------------- lifecycle

    /// <summary>
    /// Starts the supervisor loop. Returns as soon as the first connection
    /// attempt has either succeeded or been given up on; the loop keeps
    /// retrying in the background either way.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        _supervisor ??= Task.Run(() => SuperviseAsync(_lifetime.Token), CancellationToken.None);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMilliseconds(_options.CommandTimeoutMs));

        try
        {
            return await _connectedSignal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return _state == ReaderConnectionState.Connected;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendAsync(new BridgeRequest { Command = BridgeCommands.Disconnect }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnect command failed for reader {ReaderId}", ReaderId);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        CloseSocket();
        SetState(ReaderConnectionState.Disconnected, "Disconnect requested");
    }

    // ------------------------------------------------------------ operations

    public async Task<bool> StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        var ack = await SendAsync(
            new BridgeRequest { Command = BridgeCommands.StartInventory },
            cancellationToken).ConfigureAwait(false);

        var ok = ack?.Ok == true;
        _inventorying = ok;

        if (!ok)
        {
            RaiseError("startInventoryTag", ack?.Error ?? "No response from bridge", ack?.Code);
        }

        return ok;
    }

    public async Task<bool> StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        var ack = await SendAsync(
            new BridgeRequest { Command = BridgeCommands.StopInventory },
            cancellationToken).ConfigureAwait(false);

        var ok = ack?.Ok == true;

        // Treat a failed stop as stopped anyway: leaving the flag set would
        // block the next cycle on a reader that is probably already idle.
        _inventorying = false;

        if (!ok)
        {
            RaiseError("stopInventory", ack?.Error ?? "No response from bridge", ack?.Code);
        }

        return ok;
    }

    public async Task<IReadOnlyList<GpiState>> ReadGpioStateAsync(CancellationToken cancellationToken = default)
    {
        var ack = await SendAsync(
            new BridgeRequest { Command = BridgeCommands.ReadGpi },
            cancellationToken).ConfigureAwait(false);

        if (ack?.Ok != true || ack.Inputs is null)
        {
            RaiseError("inputStatus", ack?.Error ?? "No response from bridge", ack?.Code);
            return [];
        }

        var result = new List<GpiState>(ack.Inputs.Count);

        foreach (var input in ack.Inputs)
        {
            if (TryParseGpi(input.Pin, out var pin))
            {
                result.Add(new GpiState(pin, input.State != 0));
            }
        }

        return result;
    }

    public async Task<bool> SetGpioOutputAsync(
        IReadOnlyCollection<GpoCommand> outputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0)
        {
            return true;
        }

        var request = new BridgeRequest
        {
            Command = BridgeCommands.SetGpo,
            Outputs = outputs
                .Select(o => new BridgeGpoCommand { Pin = FormatGpo(o.Pin), High = o.High })
                .ToList()
        };

        var ack = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var ok = ack?.Ok == true;

        if (!ok)
        {
            RaiseError("outputOnAndOff", ack?.Error ?? "No response from bridge", ack?.Code);
        }

        return ok;
    }

    public async Task<bool> SetAntennaPowerAsync(
        IReadOnlyDictionary<int, int> powerByAntenna,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(powerByAntenna);

        if (powerByAntenna.Count == 0)
        {
            return true;
        }

        var request = new BridgeRequest
        {
            Command = BridgeCommands.SetAntennaPower,
            Power = powerByAntenna.ToDictionary(
                kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                kv => kv.Value)
        };

        var ack = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var ok = ack?.Ok == true;

        if (!ok)
        {
            RaiseError("setAntennaPower", ack?.Error ?? "No response from bridge", ack?.Code);
        }

        return ok;
    }

    public async Task<ReaderStatus> GetReaderStatusAsync(CancellationToken cancellationToken = default)
    {
        var ack = await SendAsync(
            new BridgeRequest { Command = BridgeCommands.Status },
            cancellationToken).ConfigureAwait(false);

        if (ack?.Ok != true)
        {
            // Fall back to what we last knew rather than inventing a status.
            return _lastStatus ?? new ReaderStatus
            {
                ReaderId = ReaderId,
                State = _state,
                IsInventorying = _inventorying,
                LastError = _lastError ?? ack?.Error
            };
        }

        var inputs = new List<GpiState>();

        foreach (var input in ack.Inputs ?? [])
        {
            if (TryParseGpi(input.Pin, out var pin))
            {
                inputs.Add(new GpiState(pin, input.State != 0));
            }
        }

        _lastStatus = new ReaderStatus
        {
            ReaderId = ReaderId,
            State = _state,
            IsInventorying = ack.Inventorying ?? _inventorying,
            FirmwareVersion = ack.Firmware,
            HardwareVersion = ack.Hardware,
            TemperatureCelsius = ack.Temperature,
            Inputs = inputs,
            EnabledAntennas = ack.Antennas ?? [],
            LastSeenAt = LastHeartbeat,
            ConnectedAt = _connectedAt,
            LastError = _lastError
        };

        return _lastStatus;
    }

    // ------------------------------------------------------------ supervisor

    /// <summary>
    /// Keeps a connection to the bridge alive with bounded exponential
    /// backoff. Deliberately not an aggressive retry loop: a reader that is
    /// switched off must not generate a connection storm (§30).
    /// </summary>
    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(_options.ReconnectMinSeconds);
        var max = TimeSpan.FromSeconds(_options.ReconnectMaxSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(_connectedAt is null
                    ? ReaderConnectionState.Connecting
                    : ReaderConnectionState.Reconnecting);

                await RunSessionAsync(cancellationToken).ConfigureAwait(false);

                // A clean return means the peer closed; retry from the floor.
                delay = TimeSpan.FromSeconds(_options.ReconnectMinSeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;

                _logger.LogWarning(
                    "Reader {ReaderId}: bridge session ended ({Message}); retrying in {Delay}",
                    ReaderId, ex.Message, delay);
            }
            finally
            {
                CloseSocket();

                if (_state != ReaderConnectionState.Disconnected)
                {
                    SetState(ReaderConnectionState.Disconnected, _lastError);
                }

                FailPending("Bridge connection lost");
                _inventorying = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, max.Ticks));
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };

        await client.ConnectAsync(_options.BridgeHost, _options.BridgePort, cancellationToken)
            .ConfigureAwait(false);

        _client = client;

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = false
        };

        _writer = writer;

        _logger.LogInformation(
            "Reader {ReaderId}: connected to bridge {Host}:{Port}",
            ReaderId, _options.BridgeHost, _options.BridgePort);

        // The bridge is up; now ask it to open the reader itself.
        var ack = await SendAsync(new BridgeRequest { Command = BridgeCommands.Connect }, cancellationToken)
            .ConfigureAwait(false);

        if (ack?.Ok != true)
        {
            throw new InvalidOperationException(
                $"Bridge refused to open reader {ReaderId}: {ack?.Error ?? "no response"}");
        }

        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);
        _connectedAt = DateTimeOffset.UtcNow;
        _lastError = null;

        SetState(ReaderConnectionState.Connected);
        _connectedSignal.TrySetResult(true);

        await ApplyStartupConfigurationAsync(cancellationToken).ConfigureAwait(false);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var watchdog = MonitorHeartbeatAsync(sessionCts.Token);

        try
        {
            while (!sessionCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(sessionCts.Token).ConfigureAwait(false);

                if (line is null)
                {
                    break; // peer closed
                }

                if (line.Length == 0)
                {
                    continue;
                }

                Dispatch(line);
            }
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            _connectedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Pushes the configured antenna power once the reader is open.</summary>
    private async Task ApplyStartupConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_options.AntennaPowerDbm.Count == 0)
        {
            return;
        }

        var applied = await SetAntennaPowerAsync(_options.AntennaPowerDbm, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reader {ReaderId}: antenna power {Result}", ReaderId, applied ? "applied" : "could not be applied");
    }

    /// <summary>
    /// Declares the session dead when the bridge stops sending heartbeats.
    /// A TCP socket can stay open long after the far end has stopped working,
    /// which would otherwise leave a gate armed against a reader that is gone.
    /// </summary>
    private async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatTimeoutSeconds / 3.0));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow - LastHeartbeat <= timeout)
            {
                continue;
            }

            _logger.LogWarning(
                "Reader {ReaderId}: no bridge heartbeat for {Timeout}; dropping session", ReaderId, timeout);

            _lastError = "Bridge heartbeat timeout";
            CloseSocket();

            return;
        }
    }

    private DateTimeOffset LastHeartbeat =>
        new(Interlocked.Read(ref _lastHeartbeatTicks), TimeSpan.Zero);

    // -------------------------------------------------------------- dispatch

    private void Dispatch(string line)
    {
        BridgeMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(line, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Reader {ReaderId}: unparseable bridge line discarded", ReaderId);
            return;
        }

        if (message is null)
        {
            return;
        }

        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);

        switch (message.Type)
        {
            case BridgeEvents.Ack:
                if (message.Id is { Length: > 0 } id && _pending.TryRemove(id, out var pending))
                {
                    pending.TrySetResult(message);
                }

                break;

            case BridgeEvents.Tag:
                OnTag(message);
                break;

            case BridgeEvents.Gpi:
                OnGpi(message);
                break;

            case BridgeEvents.State:
                OnConnectionState(message);
                break;

            case BridgeEvents.Error:
                _lastError = message.Message;
                RaiseError(message.Operation ?? "unknown", message.Message ?? "Unspecified reader error", message.Code);
                break;

            case BridgeEvents.Heartbeat:
                if (message.Inventorying is { } inventorying)
                {
                    _inventorying = inventorying;
                }

                break;
        }
    }

    private void OnTag(BridgeMessage message)
    {
        if (message.Epc is not { Length: > 0 } epc)
        {
            return;
        }

        var tag = new TagRead
        {
            Epc = epc,
            Tid = message.Tid,
            User = message.User,
            Pc = message.Pc,
            RssiRaw = message.Rssi,
            Rssi = ParseRssi(message.Rssi),
            AntennaRaw = message.Antenna,
            Antenna = ParseAntenna(message.Antenna),
            Count = message.Count ?? 1,
            ReaderTimestamp = message.Timestamp,
            ObservedAt = DateTimeOffset.UtcNow
        };

        try
        {
            TagRead?.Invoke(this, new TagReadEventArgs(ReaderId, tag));
        }
        catch (Exception ex)
        {
            // A subscriber fault must never break the receive loop.
            _logger.LogError(ex, "Reader {ReaderId}: tag handler threw", ReaderId);
        }
    }

    private void OnGpi(BridgeMessage message)
    {
        if (message.Pin is not { Length: > 0 } pinName || !TryParseGpi(pinName, out var pin))
        {
            return;
        }

        var high = message.State is not null && message.State != "0";

        var at = message.Timestamp is { } ticks
            ? DateTimeOffset.FromUnixTimeMilliseconds(ticks)
            : DateTimeOffset.UtcNow;

        try
        {
            GpiChanged?.Invoke(this, new GpiChangedEventArgs(ReaderId, new GpiState(pin, high), at));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader {ReaderId}: GPI handler threw", ReaderId);
        }
    }

    private void OnConnectionState(BridgeMessage message)
    {
        var connected = string.Equals(message.Connection, "CONNECTED", StringComparison.OrdinalIgnoreCase);

        if (!connected)
        {
            _lastError = message.Reason;
            _inventorying = false;
            SetState(ReaderConnectionState.Disconnected, message.Reason);

            // Drop the socket so the supervisor reconnects from a clean slate.
            CloseSocket();
        }
        else if (_state != ReaderConnectionState.Connected)
        {
            SetState(ReaderConnectionState.Connected, message.Reason);
        }
    }

    // --------------------------------------------------------------- sending

    private async Task<BridgeMessage?> SendAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        var writer = _writer;

        if (writer is null || _client?.Connected != true)
        {
            return null;
        }

        request.Id = Guid.NewGuid().ToString("N");

        var completion = new TaskCompletionSource<BridgeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = completion;

        try
        {
            var payload = JsonSerializer.Serialize(request, Json);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CommandTimeoutMs);

            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Reader {ReaderId}: command {Command} timed out after {Timeout} ms",
                ReaderId, request.Command, _options.CommandTimeoutMs);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reader {ReaderId}: command {Command} failed", ReaderId, request.Command);
            return null;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private void FailPending(string reason)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.TrySetResult(new BridgeMessage
                {
                    Type = BridgeEvents.Ack,
                    Ok = false,
                    Error = reason
                });
            }
        }
    }

    private void CloseSocket()
    {
        try
        {
            _client?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reader {ReaderId}: error closing bridge socket", ReaderId);
        }

        _client = null;
        _writer = null;
    }

    private void SetState(ReaderConnectionState next, string? reason = null)
    {
        var previous = _state;

        if (previous == next)
        {
            return;
        }

        _state = next;

        try
        {
            StateChanged?.Invoke(this, new ReaderStateChangedEventArgs(ReaderId, previous, next, reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader {ReaderId}: state handler threw", ReaderId);
        }
    }

    private void RaiseError(string operation, string message, string? code)
    {
        _lastError = message;

        _logger.LogError(
            "Reader {ReaderId}: {Operation} failed: {Message} ({Code})", ReaderId, operation, message, code);

        try
        {
            Error?.Invoke(this, new ReaderErrorEventArgs(ReaderId, operation, message, code));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader {ReaderId}: error handler threw", ReaderId);
        }
    }

    // --------------------------------------------------------------- parsing

    /// <summary>Vendor reports RSSI as a string such as "-52" or "-52.5".</summary>
    private static double? ParseRssi(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Vendor reports the antenna port as a string such as "1".</summary>
    private static int? ParseAntenna(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Maps the vendor pin names GPI1..GPI4.</summary>
    private static bool TryParseGpi(string? name, out GpiPin pin)
    {
        pin = default;

        if (name is null || name.Length != 4 || !name.StartsWith("GPI", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name[3] switch
        {
            '1' => Set(GpiPin.Gpi1, out pin),
            '2' => Set(GpiPin.Gpi2, out pin),
            '3' => Set(GpiPin.Gpi3, out pin),
            '4' => Set(GpiPin.Gpi4, out pin),
            _ => false
        };

        static bool Set(GpiPin value, out GpiPin target)
        {
            target = value;
            return true;
        }
    }

    /// <summary>Maps to the vendor constants on <c>GPOEntity</c>.</summary>
    private static string FormatGpo(GpoPin pin) => pin switch
    {
        GpoPin.Gpo1 => "GPO1",
        GpoPin.Gpo2 => "GPO2",
        GpoPin.Gpo3 => "GPO3",
        GpoPin.Gpo4 => "GPO4",
        GpoPin.WiegandData0 => "WiegandData0",
        GpoPin.WiegandData1 => "WiegandData1",
        _ => throw new ArgumentOutOfRangeException(nameof(pin), pin, "Unsupported output pin.")
    };

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reader {ReaderId}: supervisor did not stop cleanly", ReaderId);
            }
        }

        CloseSocket();
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
