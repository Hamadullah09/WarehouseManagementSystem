using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Alarms;
using Warehouse.Application.Audit;
using Warehouse.Application.Inventory;
using Warehouse.Application.Options;
using Warehouse.Application.Realtime;
using Warehouse.Domain;
using Warehouse.Domain.Entities;
using Warehouse.Domain.Gates;
using Warehouse.Domain.Validation;
using Warehouse.Rfid.Abstractions;

namespace Warehouse.Application.Gates;

public interface IGateCycleService
{
    /// <summary>Loads gates from the database and builds their runtime state.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Arms a gate so the next input edge opens a cycle.</summary>
    Task<GateSnapshot> ArmAsync(string gateCode, CancellationToken cancellationToken = default);

    /// <summary>Disarms a gate. A cycle already running is closed and validated.</summary>
    Task<GateSnapshot> DisarmAsync(string gateCode, CancellationToken cancellationToken = default);

    Task<GateSnapshot?> GetSnapshotAsync(string gateCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GateSnapshot>> GetAllSnapshotsAsync(CancellationToken cancellationToken = default);

    // Reader event sinks. Wired up by the RFID dispatcher hosted service.
    Task HandleGpiChangedAsync(string readerId, GpiState state, DateTimeOffset at);

    void HandleTagRead(string readerId, TagRead tag);

    Task HandleReaderStateChangedAsync(
        string readerId,
        ReaderConnectionState previous,
        ReaderConnectionState current,
        string? reason);

    Task HandleReaderErrorAsync(string readerId, string operation, string message, string? code);
}

/// <summary>
/// Drives the physical gate cycle end to end (§25).
/// </summary>
/// <remarks>
/// Lifecycle for one pass: input goes active, a cycle row is created and bound
/// to the gate's active document, inventory starts, reads accumulate in memory,
/// the input clears, in-flight reads drain, the EPC set freezes, the validation
/// engine rules, and only on a pass does the inventory transaction run.
///
/// Two properties matter most here. First, the read path touches no database:
/// deduplication happens in a concurrent dictionary, so a tag reported six
/// hundred times costs six hundred dictionary writes and zero queries (§36).
/// Second, every transition goes through <see cref="GateStateMachine"/> while
/// holding the gate's semaphore, so a bouncing sensor cannot open two cycles
/// and a replayed edge is reported as a duplicate rather than acted on (§28).
///
/// Registered as a singleton because it owns per-gate memory; scoped work is
/// done inside explicit service scopes.
/// </remarks>
public sealed class GateCycleService(
    IServiceScopeFactory scopeFactory,
    IRfidReaderRegistry readers,
    IValidationEngine validator,
    IGateNotifier notifier,
    IGateIndicator indicator,
    IClock clock,
    IOptionsMonitor<GateOptions> gateOptions,
    IOptionsMonitor<RfidOptions> rfidOptions,
    ILogger<GateCycleService> logger) : IGateCycleService
{
    private readonly ConcurrentDictionary<string, GateRuntime> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _gateByReader =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

        var gates = await db.Gates
            .AsNoTracking()
            .Include(g => g.Readers)
            .Where(g => g.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var gate in gates)
        {
            var reader = gate.Readers.FirstOrDefault(r => r.IsActive);

            var runtime = new GateRuntime
            {
                GateId = gate.Id,
                GateCode = gate.Code,
                ReaderId = reader?.ReaderId,
                ReaderDbId = reader?.Id,
                State = GateState.Idle,
                ReaderOnline = reader?.IsOnline ?? false
            };

            _gates[gate.Code] = runtime;

            if (reader is not null)
            {
                _gateByReader[reader.ReaderId] = gate.Code;
            }

            // A gate with a released document is immediately serviceable.
            if (gate.ActiveDocumentId is not null)
            {
                runtime.State = GateState.Ready;
            }
        }

        logger.LogInformation("Gate runtime initialised for {Count} gate(s)", _gates.Count);
    }

    // ---------------------------------------------------------------- control

    public async Task<GateSnapshot> ArmAsync(string gateCode, CancellationToken cancellationToken = default)
    {
        var runtime = Require(gateCode);

        await runtime.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (gateOptions.CurrentValue.BlockCycleWhenReaderOffline && !IsReaderHealthy(runtime))
            {
                throw new InvalidOperationException(
                    $"Gate {gateCode} cannot be armed: the reader is offline.");
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

            var document = await LoadActiveDocumentAsync(db, runtime.GateId, cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {
                throw new InvalidOperationException(
                    $"Gate {gateCode} has no released document to work against.");
            }

            if (runtime.State is GateState.Idle)
            {
                Transition(runtime, GateTrigger.AssignDocument);
            }

            if (runtime.State is GateState.Passed or GateState.Alarm or GateState.Error)
            {
                Transition(runtime, runtime.State == GateState.Alarm
                    ? GateTrigger.AcknowledgeAlarm
                    : GateTrigger.Reset);
            }

            if (!Transition(runtime, GateTrigger.Arm))
            {
                throw new InvalidOperationException(
                    $"Gate {gateCode} cannot be armed from state {runtime.State}.");
            }

            runtime.ActiveAlarm = null;
            runtime.StatusMessage = "Waiting for gate signal";

            await indicator.ClearAsync(gateCode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Lock.Release();
        }

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);

        return (await GetSnapshotAsync(gateCode, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<GateSnapshot> DisarmAsync(string gateCode, CancellationToken cancellationToken = default)
    {
        var runtime = Require(gateCode);

        if (GateStateMachine.IsCycleActive(runtime.State))
        {
            // Closing a live cycle by hand still produces a verdict; the load
            // has physically moved and must be accounted for.
            await CloseCycleAsync(runtime, GateTrigger.GateSignalOff, "Disarmed by operator")
                .ConfigureAwait(false);
        }

        await runtime.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Transition(runtime, GateTrigger.Reset);
            runtime.StatusMessage = "Gate disarmed";
        }
        finally
        {
            runtime.Lock.Release();
        }

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);

        return (await GetSnapshotAsync(gateCode, cancellationToken).ConfigureAwait(false))!;
    }

    // ----------------------------------------------------------- reader input

    public async Task HandleGpiChangedAsync(string readerId, GpiState state, DateTimeOffset at)
    {
        if (!_gateByReader.TryGetValue(readerId, out var gateCode)
            || !_gates.TryGetValue(gateCode, out var runtime))
        {
            return;
        }

        var config = FindReaderOptions(readerId);

        if (config is null)
        {
            return;
        }

        // Only the line wired to the gate drives the cycle. Other inputs are
        // still recorded, which is what makes a miswired install diagnosable.
        var isGateLine = state.Pin == config.Gpio.GateSignalInput;
        var active = config.Gpio.GateSignalActiveHigh ? state.High : !state.High;

        // Claim the read window before any I/O at all. Opening a cycle costs a
        // database round trip, and on a fast conveyor tags arrive inside it;
        // staging starts here so none of them are lost.
        if (isGateLine && active)
        {
            runtime.StagedReads.Clear();
            runtime.IsOpeningCycle = true;
        }

        try
        {
            // Cycle handling runs before the audit write: on a real reader in
            // command mode this is the path to startInventoryTag, and every
            // millisecond spent here is antenna time lost.
            if (isGateLine)
            {
                if (active)
                {
                    await OpenCycleAsync(runtime, config, at).ConfigureAwait(false);
                }
                else
                {
                    await CloseCycleAsync(runtime, GateTrigger.GateSignalOff, "Gate signal cleared")
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (isGateLine && active)
            {
                runtime.IsOpeningCycle = false;
                runtime.StagedReads.Clear();
            }
        }

        await RecordGpioEventAsync(runtime, readerId, state.Pin.ToString(), state.High, isInput: true, at)
            .ConfigureAwait(false);

        await SafeNotifyAsync(() => notifier.GpioChangedAsync(new GpioStateUpdate
        {
            GateCode = runtime.GateCode,
            ReaderId = readerId,
            Pin = state.Pin.ToString(),
            High = state.High,
            IsInput = true,
            Timestamp = at
        })).ConfigureAwait(false);
    }

    public void HandleTagRead(string readerId, TagRead tag)
    {
        // Hot path. No I/O, no locking, no allocation beyond the accumulator.
        if (!_gateByReader.TryGetValue(readerId, out var gateCode)
            || !_gates.TryGetValue(gateCode, out var runtime))
        {
            return;
        }

        var epc = Epc.Normalize(tag.Epc);

        if (epc.Length == 0)
        {
            return;
        }

        var cycle = runtime.Cycle;

        if (cycle is null)
        {
            if (runtime.IsOpeningCycle)
            {
                // The edge has been seen but the cycle row does not exist yet.
                // Hold the read; OpenCycleAsync drains this queue.
                runtime.StagedReads.Enqueue(new StagedRead(epc, tag.Rssi, tag.Antenna, tag.ObservedAt));
                runtime.LastEpc = epc;

                return;
            }

            logger.LogDebug(
                "Ignoring out-of-cycle read {Epc} on gate {GateCode}", tag.Epc, runtime.GateCode);

            return;
        }

        if (!GateStateMachine.IsCycleActive(runtime.State))
        {
            logger.LogDebug(
                "Ignoring read {Epc} on gate {GateCode} in state {State}",
                tag.Epc, runtime.GateCode, runtime.State);

            return;
        }

        var (_, isNew) = cycle.Observe(epc, tag.Rssi, tag.Antenna, tag.ObservedAt);

        runtime.LastEpc = epc;

        if (isNew)
        {
            cycle.NewEpcs.Writer.TryWrite(epc);
        }
    }

    public async Task HandleReaderStateChangedAsync(
        string readerId,
        ReaderConnectionState previous,
        ReaderConnectionState current,
        string? reason)
    {
        if (!_gateByReader.TryGetValue(readerId, out var gateCode)
            || !_gates.TryGetValue(gateCode, out var runtime))
        {
            return;
        }

        var online = current == ReaderConnectionState.Connected;
        runtime.ReaderOnline = online;

        await PersistReaderStateAsync(runtime, readerId, online, current, reason).ConfigureAwait(false);

        await SafeNotifyAsync(() => notifier.ReaderStatusChangedAsync(new ReaderStatusUpdate
        {
            ReaderId = readerId,
            GateCode = runtime.GateCode,
            Online = online,
            Message = reason,
            Timestamp = clock.UtcNow
        })).ConfigureAwait(false);

        if (!online)
        {
            // Losing the reader mid-cycle invalidates the evidence: abort
            // rather than validate against a partial read (§29).
            if (GateStateMachine.IsCycleActive(runtime.State))
            {
                await AbortCycleAsync(runtime, "Reader disconnected during cycle").ConfigureAwait(false);
            }

            await runtime.Lock.WaitAsync().ConfigureAwait(false);

            try
            {
                Transition(runtime, GateTrigger.ReaderLost);
                runtime.StatusMessage = "RFID reader offline";
            }
            finally
            {
                runtime.Lock.Release();
            }

            await RaiseAlarmAsync(runtime, AlarmType.ReaderDisconnected,
                $"Reader {readerId} went offline. {reason}".TrimEnd(), null, []).ConfigureAwait(false);
        }
        else if (runtime.State == GateState.ReaderDisconnected)
        {
            await runtime.Lock.WaitAsync().ConfigureAwait(false);

            try
            {
                Transition(runtime, GateTrigger.ReaderRestored);
                runtime.StatusMessage = "Reader online";
            }
            finally
            {
                runtime.Lock.Release();
            }
        }

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task HandleReaderErrorAsync(string readerId, string operation, string message, string? code)
    {
        if (!_gateByReader.TryGetValue(readerId, out var gateCode)
            || !_gates.TryGetValue(gateCode, out var runtime))
        {
            return;
        }

        // Mark the live cycle tainted. The validation engine refuses to pass a
        // cycle whose reader misbehaved, so this alone blocks a bad movement.
        runtime.Cycle?.MarkReaderUnhealthy();

        logger.LogError(
            "Reader {ReaderId} error during {Operation}: {Message} ({Code})",
            readerId, operation, message, code);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

        if (runtime.ReaderDbId is { } dbId)
        {
            db.ReaderEvents.Add(new ReaderEvent
            {
                ReaderId = dbId,
                EventType = ReaderEventType.Error,
                Message = message,
                SdkOperation = operation,
                ErrorCode = code,
                OccurredAt = clock.UtcNow
            });

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------- the cycle

    private async Task OpenCycleAsync(GateRuntime runtime, RfidReaderOptions config, DateTimeOffset at)
    {
        await runtime.Lock.WaitAsync().ConfigureAwait(false);

        ActiveCycle? cycle = null;

        try
        {
            var options = gateOptions.CurrentValue;

            // Contact bounce and event replay both look like a second rising
            // edge. Neither may open a second cycle.
            if (runtime.LastAcceptedEdgeAt is { } last
                && (at - last).TotalMilliseconds < options.MinimumCycleIntervalMs)
            {
                logger.LogWarning(
                    "Suppressed duplicate gate edge on {GateCode} ({Delta} ms since last)",
                    runtime.GateCode, (at - last).TotalMilliseconds);

                _ = RaiseAlarmAsync(runtime, AlarmType.DuplicateGateEvent,
                    "A second gate signal arrived inside the minimum cycle interval and was ignored.",
                    null, []);

                return;
            }

            if (!GateStateMachine.CanStartCycle(runtime.State))
            {
                logger.LogWarning(
                    "Gate {GateCode} received a start signal in state {State}; ignored",
                    runtime.GateCode, runtime.State);

                if (runtime.State == GateState.ReaderDisconnected)
                {
                    _ = RaiseAlarmAsync(runtime, AlarmType.ReaderDisconnected,
                        "A gate signal arrived while the reader was offline. The movement was not recorded.",
                        null, []);
                }
                else if (GateStateMachine.IsCycleActive(runtime.State))
                {
                    _ = RaiseAlarmAsync(runtime, AlarmType.DuplicateGateEvent,
                        $"A gate signal arrived while a cycle was already {runtime.State}.", null, []);
                }

                return;
            }

            if (options.BlockCycleWhenReaderOffline && !IsReaderHealthy(runtime))
            {
                _ = RaiseAlarmAsync(runtime, AlarmType.ReaderDisconnected,
                    "A gate signal arrived but the reader was not healthy. The cycle was refused.", null, []);

                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IWarehouseDbContext>();
            var numbers = sp.GetRequiredService<INumberGenerator>();
            var audit = sp.GetRequiredService<IAuditService>();

            var document = await LoadActiveDocumentAsync(db, runtime.GateId, CancellationToken.None)
                .ConfigureAwait(false);

            var expected = document?.Items
                .Where(i => !i.IsDetected)
                .Select(i => i.Epc)
                .ToHashSet(Epc.Comparer) ?? new HashSet<string>(Epc.Comparer);

            var triggerKey = $"{runtime.GateCode}|{at.ToUnixTimeMilliseconds()}";

            var duplicate = await db.GateCycles
                .AnyAsync(c => c.TriggerKey == triggerKey, CancellationToken.None)
                .ConfigureAwait(false);

            if (duplicate)
            {
                logger.LogWarning("Gate edge {TriggerKey} was already processed; ignoring replay", triggerKey);

                _ = RaiseAlarmAsync(runtime, AlarmType.DuplicateGateEvent,
                    "This gate event had already been processed and was not run again.", null, []);

                return;
            }

            var entity = new GateCycle
            {
                CycleId = await numbers.NextCycleIdAsync(CancellationToken.None).ConfigureAwait(false),
                TriggerKey = triggerKey,
                GateId = runtime.GateId,
                ReaderId = runtime.ReaderDbId ?? 0,
                DocumentId = document?.Id,
                Status = GateCycleStatus.Running,
                StartedAt = at,
                ExpectedEpcCount = expected.Count
            };

            db.GateCycles.Add(entity);

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.GpioOn,
                GateId = runtime.GateId,
                ReaderId = runtime.ReaderDbId,
                CycleId = entity.CycleId,
                DocumentNumber = document?.DocumentNumber,
                Details = $"{config.Gpio.GateSignalInput} active"
            });

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.GateCycleStarted,
                GateId = runtime.GateId,
                DocumentId = document?.Id,
                DocumentNumber = document?.DocumentNumber,
                CycleId = entity.CycleId,
                ReaderId = runtime.ReaderDbId,
                NewState = GateCycleStatus.Running.ToString(),
                Result = $"{expected.Count} EPCs expected"
            });

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            cycle = new ActiveCycle
            {
                Id = entity.Id,
                CycleId = entity.CycleId,
                StartedAt = at,
                DocumentId = document?.Id,
                DocumentNumber = document?.DocumentNumber,
                DocumentType = document?.Type,
                ExpectedEpcs = expected
            };

            // Publish the cycle first, then drain: a read arriving between the
            // two lands directly on the cycle rather than in the queue.
            runtime.Cycle = cycle;

            while (runtime.StagedReads.TryDequeue(out var staged))
            {
                var (_, isNew) = cycle.Observe(staged.Epc, staged.Rssi, staged.Antenna, staged.At);

                if (isNew)
                {
                    cycle.NewEpcs.Writer.TryWrite(staged.Epc);
                }
            }

            runtime.LastAcceptedEdgeAt = at;
            runtime.ActiveAlarm = null;

            Transition(runtime, GateTrigger.GateSignalOn);
            runtime.StatusMessage = "Reading RFID";

            if (document is not null)
            {
                await MarkDocumentInProgressAsync(db, document.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            runtime.Lock.Release();
        }

        if (cycle is null)
        {
            return;
        }

        // Firmware trigger mode means the reader already started on the edge;
        // issuing a redundant start would fight the device.
        if (config.TriggerMode == InventoryTriggerMode.Command
            && readers.TryGet(runtime.ReaderId ?? string.Empty, out var reader))
        {
            var started = await reader.StartInventoryAsync(CancellationToken.None).ConfigureAwait(false);

            if (!started)
            {
                cycle.MarkReaderUnhealthy();

                logger.LogError(
                    "Failed to start inventory on gate {GateCode} cycle {CycleId}",
                    runtime.GateCode, cycle.CycleId);
            }
        }

        runtime.ConsumerTask = Task.Run(() => ConsumeNewEpcsAsync(runtime, cycle));

        _ = ArmTimeoutAsync(runtime, cycle);

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Force-closes a cycle whose input never cleared.</summary>
    private async Task ArmTimeoutAsync(GateRuntime runtime, ActiveCycle cycle)
    {
        var timeout = gateOptions.CurrentValue.CycleTimeoutMs;

        try
        {
            await Task.Delay(timeout, cycle.TimeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(runtime.Cycle, cycle))
        {
            return;
        }

        logger.LogWarning(
            "Cycle {CycleId} on gate {GateCode} exceeded {Timeout} ms; forcing closure",
            cycle.CycleId, runtime.GateCode, timeout);

        await RaiseAlarmAsync(runtime, AlarmType.Timeout,
            $"Cycle {cycle.CycleId} ran longer than {timeout} ms and was closed automatically.",
            null, []).ConfigureAwait(false);

        await CloseCycleAsync(runtime, GateTrigger.Timeout, "Cycle timed out").ConfigureAwait(false);
    }

    private async Task CloseCycleAsync(GateRuntime runtime, GateTrigger trigger, string reason)
    {
        ActiveCycle? cycle;

        await runtime.Lock.WaitAsync().ConfigureAwait(false);

        try
        {
            cycle = runtime.Cycle;

            if (cycle is null || !GateStateMachine.IsCycleActive(runtime.State))
            {
                return;
            }

            if (!Transition(runtime, trigger))
            {
                return;
            }

            runtime.StatusMessage = "Processing";
            await cycle.TimeoutSource.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            runtime.Lock.Release();
        }

        var config = FindReaderOptions(runtime.ReaderId ?? string.Empty);

        if (config?.TriggerMode == InventoryTriggerMode.Command
            && readers.TryGet(runtime.ReaderId ?? string.Empty, out var reader))
        {
            var stopped = await reader.StopInventoryAsync(CancellationToken.None).ConfigureAwait(false);

            if (!stopped)
            {
                cycle.MarkReaderUnhealthy();
            }
        }

        await RecordGpioEventAsync(
            runtime,
            runtime.ReaderId ?? string.Empty,
            config?.Gpio.GateSignalInput.ToString() ?? "GPI1",
            high: false,
            isInput: true,
            clock.UtcNow,
            cycle.Id).ConfigureAwait(false);

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);

        // Reads already in flight when the signal dropped still belong to this
        // cycle; dropping them would manufacture a missing-EPC alarm.
        var drain = gateOptions.CurrentValue.DrainMs;

        if (drain > 0)
        {
            await Task.Delay(drain).ConfigureAwait(false);
        }

        cycle.NewEpcs.Writer.TryComplete();

        if (runtime.ConsumerTask is { } consumer)
        {
            try
            {
                await consumer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("EPC consumer for cycle {CycleId} did not drain in time", cycle.CycleId);
            }
        }

        await ValidateAndFinaliseAsync(runtime, cycle, reason).ConfigureAwait(false);
    }

    private async Task ValidateAndFinaliseAsync(GateRuntime runtime, ActiveCycle cycle, string reason)
    {
        await runtime.Lock.WaitAsync().ConfigureAwait(false);

        try
        {
            Transition(runtime, GateTrigger.BeginValidation);
            runtime.StatusMessage = "Validating";
        }
        finally
        {
            runtime.Lock.Release();
        }

        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IWarehouseDbContext>();
        var audit = sp.GetRequiredService<IAuditService>();
        var alarms = sp.GetRequiredService<IAlarmService>();
        var inventory = sp.GetRequiredService<IInventoryService>();

        var detected = cycle.Epcs.Keys.ToList();

        // One batched lookup for the whole cycle, not one per read (§36).
        var known = await db.EpcTags
            .Where(t => detected.Contains(t.Epc))
            .Select(t => new { t.Epc, t.IsActive, t.Status })
            .ToListAsync()
            .ConfigureAwait(false);

        var knownSet = known.Select(k => k.Epc).ToHashSet(Epc.Comparer);

        var blockedSet = known
            .Where(k => !k.IsActive || k.Status is EpcStatus.Retired or EpcStatus.Blocked)
            .Select(k => k.Epc)
            .ToHashSet(Epc.Comparer);

        var options = gateOptions.CurrentValue;

        var result = validator.Validate(new ValidationInput
        {
            DocumentType = cycle.DocumentType ?? DocumentType.Inward,
            ExpectedEpcs = cycle.ExpectedEpcs.ToList(),
            DetectedEpcs = detected,
            KnownEpcs = knownSet,
            BlockedEpcs = blockedSet,
            ReaderHealthy = cycle.ReaderHealthy,
            Policy = new ValidationPolicy
            {
                RequireAllExpected = options.RequireAllExpected,
                FailOnUnknown = options.FailOnUnknownEpc,
                FailOnUnexpected = options.FailOnUnexpectedEpc,
                FailOnEmpty = options.FailOnEmptyRead,
                RequireHealthyReader = options.RequireHealthyReader,
                MaxEpcsPerCycle = options.MaxEpcsPerCycle
            }
        });

        await PersistCycleResultAsync(db, audit, cycle, result, reason).ConfigureAwait(false);

        var committed = false;

        if (result.IsPass && cycle.DocumentId is { } documentId)
        {
            var commit = await inventory.CommitCycleAsync(new InventoryCommitRequest
            {
                GateCycleId = cycle.Id,
                CycleId = cycle.CycleId,
                DocumentId = documentId,
                MatchedEpcs = result.Matched,
                GateId = runtime.GateId
            }).ConfigureAwait(false);

            committed = commit.Committed;
        }

        await runtime.Lock.WaitAsync().ConfigureAwait(false);

        try
        {
            Transition(runtime, result.IsPass ? GateTrigger.ValidationPassed : GateTrigger.ValidationFailed);

            runtime.StatusMessage = result.IsPass ? "Pass" : result.Summary;
            runtime.ActiveAlarm = result.PrimaryAlarm;
            runtime.Cycle = null;
        }
        finally
        {
            runtime.Lock.Release();
        }

        if (result.IsPass)
        {
            await indicator.SignalPassAsync(runtime.GateCode).ConfigureAwait(false);
        }
        else
        {
            await RaiseValidationAlarmsAsync(runtime, cycle, result, alarms).ConfigureAwait(false);
        }

        var status = cycle.DocumentId is null
            ? (DocumentStatus?)null
            : await db.Documents
                .Where(d => d.Id == cycle.DocumentId)
                .Select(d => (DocumentStatus?)d.Status)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

        await SafeNotifyAsync(() => notifier.CycleCompletedAsync(new CycleCompletedUpdate
        {
            GateCode = runtime.GateCode,
            CycleId = cycle.CycleId,
            Passed = result.IsPass,
            DocumentNumber = cycle.DocumentNumber,
            DocumentStatus = status,
            ExpectedCount = result.ExpectedCount,
            DetectedCount = result.DetectedCount,
            Missing = result.Missing,
            Unknown = result.Unknown,
            Unexpected = result.Unexpected,
            Summary = result.Summary,
            Timestamp = clock.UtcNow
        })).ConfigureAwait(false);

        logger.LogInformation(
            "Cycle {CycleId} on {GateCode}: {Summary} (inventory committed: {Committed})",
            cycle.CycleId, runtime.GateCode, result.Summary, committed);

        await MaybeRearmAsync(runtime, result.IsPass).ConfigureAwait(false);
        await PublishStatusAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistCycleResultAsync(
        IWarehouseDbContext db,
        IAuditService audit,
        ActiveCycle cycle,
        ValidationResult result,
        string reason)
    {
        var entity = await db.GateCycles.FirstOrDefaultAsync(c => c.Id == cycle.Id).ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        entity.Status = result.IsPass ? GateCycleStatus.Passed : GateCycleStatus.Failed;
        entity.CompletedAt = clock.UtcNow;
        entity.DetectedEpcCount = result.DetectedCount;
        entity.RawReadCount = cycle.RawReadCount;
        entity.ExpectedEpcCount = result.ExpectedCount;
        entity.UnknownEpcCount = result.Unknown.Count;
        entity.UnexpectedEpcCount = result.Unexpected.Count;
        entity.MissingEpcCount = result.Missing.Count;
        entity.ValidationResult = result.Outcome;
        entity.ValidationSummary = result.Summary;
        entity.ReaderHealthy = cycle.ReaderHealthy;

        foreach (var epc in result.Matched)
        {
            db.GateCycleEpcs.Add(BuildCycleEpc(cycle, epc, EpcClassification.Expected));
        }

        foreach (var epc in result.Unknown)
        {
            db.GateCycleEpcs.Add(BuildCycleEpc(cycle, epc, EpcClassification.Unknown));

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.UnknownEpc,
                GateCycleId = cycle.Id,
                CycleId = cycle.CycleId,
                DocumentId = cycle.DocumentId,
                DocumentNumber = cycle.DocumentNumber,
                Epc = epc,
                Result = "UNKNOWN_EPC"
            });
        }

        foreach (var epc in result.Unexpected)
        {
            db.GateCycleEpcs.Add(BuildCycleEpc(cycle, epc, EpcClassification.Unexpected));

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.UnexpectedEpc,
                GateCycleId = cycle.Id,
                CycleId = cycle.CycleId,
                DocumentId = cycle.DocumentId,
                DocumentNumber = cycle.DocumentNumber,
                Epc = epc,
                Result = "UNEXPECTED_EPC"
            });
        }

        foreach (var epc in result.Missing)
        {
            // Recorded with no read data: the point is that it was never seen.
            db.GateCycleEpcs.Add(new GateCycleEpc
            {
                GateCycleId = cycle.Id,
                Epc = epc,
                Classification = EpcClassification.Missing
            });

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.MissingEpc,
                GateCycleId = cycle.Id,
                CycleId = cycle.CycleId,
                DocumentId = cycle.DocumentId,
                DocumentNumber = cycle.DocumentNumber,
                Epc = epc,
                Result = "MISSING_EPC"
            });
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.GateCycleCompleted,
            GateCycleId = cycle.Id,
            CycleId = cycle.CycleId,
            DocumentId = cycle.DocumentId,
            DocumentNumber = cycle.DocumentNumber,
            NewState = entity.Status.ToString(),
            Result = result.Summary,
            Details = reason
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private GateCycleEpc BuildCycleEpc(ActiveCycle cycle, string epc, EpcClassification classification)
    {
        cycle.Epcs.TryGetValue(epc, out var accumulator);

        return new GateCycleEpc
        {
            GateCycleId = cycle.Id,
            Epc = epc,
            Classification = classification,
            ReadCount = accumulator?.ReadCount ?? 0,
            PeakRssi = accumulator?.PeakRssi,
            Antenna = accumulator?.Antenna,
            FirstSeenAt = accumulator?.FirstSeenAt,
            LastSeenAt = accumulator?.LastSeenAt
        };
    }

    private async Task RaiseValidationAlarmsAsync(
        GateRuntime runtime,
        ActiveCycle cycle,
        ValidationResult result,
        IAlarmService alarms)
    {
        var requests = new List<RaiseAlarmRequest>();

        foreach (var alarm in result.Alarms)
        {
            var (message, epc, list) = alarm switch
            {
                AlarmType.UnknownEpc => (
                    $"Unknown EPC detected at gate {runtime.GateCode}. "
                        + $"{result.Unknown.Count} tag(s) are not registered in the warehouse.",
                    result.Unknown.FirstOrDefault(),
                    (IReadOnlyList<string>)result.Unknown),

                AlarmType.UnexpectedEpc => (
                    $"Known but unexpected EPC detected at gate {runtime.GateCode}. "
                        + $"{result.Unexpected.Count} tag(s) are not on {cycle.DocumentNumber ?? "the active document"}.",
                    result.Unexpected.FirstOrDefault(),
                    (IReadOnlyList<string>)result.Unexpected),

                AlarmType.MissingEpc => (
                    $"Incomplete movement at gate {runtime.GateCode}. Expected {result.ExpectedCount}, "
                        + $"detected {result.DetectedCount}, missing {result.Missing.Count}.",
                    result.Missing.FirstOrDefault(),
                    (IReadOnlyList<string>)result.Missing),

                AlarmType.NoEpc => (
                    $"No EPC detected at gate {runtime.GateCode}. An item may have passed without an RFID tag.",
                    null,
                    (IReadOnlyList<string>)[]),

                AlarmType.ReaderError => (
                    $"The reader on gate {runtime.GateCode} reported a fault during the cycle. "
                        + "The movement was not accepted.",
                    null,
                    (IReadOnlyList<string>)[]),

                AlarmType.DocumentMismatch => (
                    $"Cycle read {result.DetectedCount} EPCs at gate {runtime.GateCode}, "
                        + "which exceeds the configured ceiling for one pass.",
                    null,
                    (IReadOnlyList<string>)[]),

                _ => ($"Validation failed at gate {runtime.GateCode}: {result.Summary}",
                    null,
                    (IReadOnlyList<string>)[])
            };

            requests.Add(new RaiseAlarmRequest
            {
                AlarmType = alarm,
                Message = message,
                GateId = runtime.GateId,
                GateCode = runtime.GateCode,
                DocumentId = cycle.DocumentId,
                DocumentNumber = cycle.DocumentNumber,
                GateCycleId = cycle.Id,
                CycleId = cycle.CycleId,
                ReaderId = runtime.ReaderDbId,
                Epc = epc,
                Epcs = list
            });
        }

        if (requests.Count > 0)
        {
            await alarms.RaiseManyAsync(requests).ConfigureAwait(false);
        }
    }

    private async Task AbortCycleAsync(GateRuntime runtime, string reason)
    {
        var cycle = runtime.Cycle;

        if (cycle is null)
        {
            return;
        }

        await cycle.TimeoutSource.CancelAsync().ConfigureAwait(false);
        cycle.NewEpcs.Writer.TryComplete();

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IWarehouseDbContext>();
        var audit = sp.GetRequiredService<IAuditService>();

        var entity = await db.GateCycles.FirstOrDefaultAsync(c => c.Id == cycle.Id).ConfigureAwait(false);

        if (entity is not null)
        {
            entity.Status = GateCycleStatus.Aborted;
            entity.CompletedAt = clock.UtcNow;
            entity.DetectedEpcCount = cycle.Epcs.Count;
            entity.RawReadCount = cycle.RawReadCount;
            entity.ReaderHealthy = false;
            entity.ValidationSummary = reason;
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.GateCycleAborted,
            GateId = runtime.GateId,
            GateCycleId = cycle.Id,
            CycleId = cycle.CycleId,
            DocumentId = cycle.DocumentId,
            DocumentNumber = cycle.DocumentNumber,
            NewState = GateCycleStatus.Aborted.ToString(),
            Details = reason
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        runtime.Cycle = null;

        logger.LogWarning("Cycle {CycleId} aborted: {Reason}", cycle.CycleId, reason);
    }

    private async Task MaybeRearmAsync(GateRuntime runtime, bool passed)
    {
        var options = gateOptions.CurrentValue;
        var rearm = passed ? options.AutoRearmAfterPass : options.AutoRearmAfterAlarm;

        if (!rearm)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

        var document = await LoadActiveDocumentAsync(db, runtime.GateId, CancellationToken.None)
            .ConfigureAwait(false);

        if (document is null)
        {
            // Nothing left to do at this gate.
            await runtime.Lock.WaitAsync().ConfigureAwait(false);

            try
            {
                Transition(runtime, GateTrigger.ReleaseDocument);
                runtime.StatusMessage = "Document complete";
            }
            finally
            {
                runtime.Lock.Release();
            }

            return;
        }

        await runtime.Lock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (runtime.State == GateState.Alarm)
            {
                Transition(runtime, GateTrigger.AcknowledgeAlarm);
            }

            if (Transition(runtime, GateTrigger.Arm))
            {
                runtime.StatusMessage = "Waiting for gate signal";
            }
        }
        finally
        {
            runtime.Lock.Release();
        }
    }

    // ------------------------------------------------------------- background

    /// <summary>
    /// Classifies and broadcasts newly-sighted EPCs off the reader callback
    /// thread, so a slow display can never back-pressure the SDK.
    /// </summary>
    private async Task ConsumeNewEpcsAsync(GateRuntime runtime, ActiveCycle cycle)
    {
        try
        {
            await foreach (var epc in cycle.NewEpcs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var isExpected = cycle.ExpectedEpcs.Contains(epc);
                bool isKnown;

                if (isExpected)
                {
                    isKnown = true;
                }
                else
                {
                    // Only anomalies reach the database, and only once each,
                    // so the display can honestly distinguish an unknown tag
                    // from a known-but-unexpected one while the gate is live.
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

                    isKnown = await db.EpcTags.AnyAsync(t => t.Epc == epc).ConfigureAwait(false);
                }

                if (cycle.Epcs.TryGetValue(epc, out var accumulator))
                {
                    accumulator.IsKnown = isKnown;
                }

                await SafeNotifyAsync(() => notifier.EpcDetectedAsync(new EpcDetectedUpdate
                {
                    GateCode = runtime.GateCode,
                    CycleId = cycle.CycleId,
                    Epc = epc,
                    IsKnown = isKnown,
                    IsExpected = isExpected,
                    DetectedCount = cycle.Epcs.Count,
                    ExpectedCount = cycle.ExpectedEpcs.Count,
                    Rssi = accumulator?.PeakRssi,
                    Antenna = accumulator?.Antenna,
                    Timestamp = clock.UtcNow
                })).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EPC consumer failed for cycle {CycleId}", cycle.CycleId);
        }
    }

    // ---------------------------------------------------------------- queries

    public async Task<GateSnapshot?> GetSnapshotAsync(
        string gateCode,
        CancellationToken cancellationToken = default)
    {
        if (!_gates.TryGetValue(gateCode, out var runtime))
        {
            return null;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

        return await BuildSnapshotAsync(db, runtime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GateSnapshot>> GetAllSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

        var result = new List<GateSnapshot>(_gates.Count);

        foreach (var runtime in _gates.Values)
        {
            result.Add(await BuildSnapshotAsync(db, runtime, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<GateSnapshot> BuildSnapshotAsync(
        IWarehouseDbContext db,
        GateRuntime runtime,
        CancellationToken cancellationToken)
    {
        var gate = await db.Gates
            .AsNoTracking()
            .Where(g => g.Id == runtime.GateId)
            .Select(g => new { g.Name })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var document = await LoadActiveDocumentAsync(db, runtime.GateId, cancellationToken)
            .ConfigureAwait(false);

        var balance = document?.Items
            .Where(i => !i.IsDetected)
            .Select(i => i.Epc)
            .OrderBy(e => e, Epc.Comparer)
            .ToList() ?? [];

        return new GateSnapshot
        {
            GateCode = runtime.GateCode,
            GateName = gate?.Name ?? runtime.GateCode,
            State = runtime.State,
            ReaderOnline = runtime.ReaderOnline,
            ReaderId = runtime.ReaderId,
            CycleId = runtime.Cycle?.CycleId,
            CycleStartedAt = runtime.Cycle?.StartedAt,
            DocumentNumber = document?.DocumentNumber,
            MovementType = document?.Type,
            UserDisplayName = document?.UserDisplayName,
            ExpectedArticles = document?.ExpectedArticles ?? 0,
            DetectedArticles = document?.DetectedArticles ?? 0,
            BalanceArticles = document?.BalanceArticles ?? 0,
            ExpectedQuantity = document?.ExpectedQuantity ?? 0,
            DetectedQuantity = document?.DetectedQuantity ?? 0,
            BalanceQuantity = document?.BalanceQuantity ?? 0,
            CycleDetectedCount = runtime.Cycle?.Epcs.Count ?? 0,
            BalanceEpcs = balance,
            LastEpc = runtime.LastEpc,
            StatusMessage = runtime.StatusMessage,
            ActiveAlarm = runtime.ActiveAlarm,
            Timestamp = clock.UtcNow
        };
    }

    // ---------------------------------------------------------------- helpers

    private static Task<Document?> LoadActiveDocumentAsync(
        IWarehouseDbContext db,
        int gateId,
        CancellationToken cancellationToken) =>
        db.Documents
            .AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.GateId == gateId
                     && (d.Status == DocumentStatus.Released || d.Status == DocumentStatus.InProgress))
            .OrderBy(d => d.ReleasedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task MarkDocumentInProgressAsync(
        IWarehouseDbContext db,
        int documentId,
        CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is { Status: DocumentStatus.Released })
        {
            document.Status = DocumentStatus.InProgress;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private GateRuntime Require(string gateCode) =>
        _gates.TryGetValue(gateCode, out var runtime)
            ? runtime
            : throw new KeyNotFoundException($"Gate {gateCode} is not configured.");

    private RfidReaderOptions? FindReaderOptions(string readerId) =>
        rfidOptions.CurrentValue.Readers.FirstOrDefault(
            r => string.Equals(r.ReaderId, readerId, StringComparison.OrdinalIgnoreCase));

    private bool IsReaderHealthy(GateRuntime runtime) =>
        runtime.ReaderOnline
        && runtime.ReaderId is { } id
        && readers.TryGet(id, out var reader)
        && reader.State == ReaderConnectionState.Connected;

    /// <summary>Applies a transition under the caller's lock. Returns false when illegal.</summary>
    private bool Transition(GateRuntime runtime, GateTrigger trigger)
    {
        var next = GateStateMachine.Next(runtime.State, trigger);

        if (next is null)
        {
            logger.LogDebug(
                "Rejected transition {Trigger} from {State} on gate {GateCode}",
                trigger, runtime.State, runtime.GateCode);

            return false;
        }

        logger.LogDebug(
            "Gate {GateCode}: {From} --{Trigger}--> {To}",
            runtime.GateCode, runtime.State, trigger, next.Value);

        runtime.State = next.Value;

        return true;
    }

    private async Task RaiseAlarmAsync(
        GateRuntime runtime,
        AlarmType type,
        string message,
        string? epc,
        IReadOnlyList<string> epcs)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var alarms = scope.ServiceProvider.GetRequiredService<IAlarmService>();

            await alarms.RaiseAsync(new RaiseAlarmRequest
            {
                AlarmType = type,
                Message = message,
                GateId = runtime.GateId,
                GateCode = runtime.GateCode,
                GateCycleId = runtime.Cycle?.Id,
                CycleId = runtime.Cycle?.CycleId,
                DocumentId = runtime.Cycle?.DocumentId,
                DocumentNumber = runtime.Cycle?.DocumentNumber,
                ReaderId = runtime.ReaderDbId,
                Epc = epc,
                Epcs = epcs
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to raise {AlarmType} alarm on gate {GateCode}", type, runtime.GateCode);
        }
    }

    private async Task RecordGpioEventAsync(
        GateRuntime runtime,
        string readerId,
        string pin,
        bool high,
        bool isInput,
        DateTimeOffset at,
        long? cycleId = null)
    {
        if (runtime.ReaderDbId is not { } dbId)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

            db.GpioEvents.Add(new GpioEvent
            {
                ReaderId = dbId,
                GateId = runtime.GateId,
                Pin = pin,
                IsInput = isInput,
                High = high,
                GateCycleId = cycleId ?? runtime.Cycle?.Id,
                OccurredAt = at
            });

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record GPIO event {Pin}={High} on {ReaderId}", pin, high, readerId);
        }
    }

    private async Task PersistReaderStateAsync(
        GateRuntime runtime,
        string readerId,
        bool online,
        ReaderConnectionState state,
        string? reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IWarehouseDbContext>();
            var audit = sp.GetRequiredService<IAuditService>();

            var reader = await db.Readers.FirstOrDefaultAsync(r => r.ReaderId == readerId).ConfigureAwait(false);

            if (reader is null)
            {
                return;
            }

            reader.IsOnline = online;
            reader.LastSeenAt = clock.UtcNow;

            if (online)
            {
                reader.ConnectedAt = clock.UtcNow;
                reader.LastError = null;
            }
            else
            {
                reader.IsInventorying = false;
                reader.LastError = reason;
                reader.LastErrorAt = clock.UtcNow;
            }

            db.ReaderEvents.Add(new ReaderEvent
            {
                ReaderId = reader.Id,
                EventType = online ? ReaderEventType.Connected : ReaderEventType.Disconnected,
                Message = reason ?? state.ToString(),
                OccurredAt = clock.UtcNow
            });

            audit.Enlist(new AuditEntry
            {
                Action = online ? AuditAction.ReaderConnected : AuditAction.ReaderDisconnected,
                GateId = runtime.GateId,
                ReaderId = reader.Id,
                NewState = state.ToString(),
                Details = reason
            });

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist reader state for {ReaderId}", readerId);
        }
    }

    private async Task PublishStatusAsync(GateRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IWarehouseDbContext>();

            var snapshot = await BuildSnapshotAsync(db, runtime, cancellationToken).ConfigureAwait(false);

            await notifier.GateStatusChangedAsync(new GateStatusUpdate
            {
                GateCode = snapshot.GateCode,
                State = snapshot.State,
                DocumentNumber = snapshot.DocumentNumber,
                MovementType = snapshot.MovementType,
                UserDisplayName = snapshot.UserDisplayName,
                CycleId = snapshot.CycleId,
                ExpectedArticles = snapshot.ExpectedArticles,
                DetectedArticles = snapshot.DetectedArticles,
                BalanceArticles = snapshot.BalanceArticles,
                ExpectedQuantity = snapshot.ExpectedQuantity,
                DetectedQuantity = snapshot.DetectedQuantity,
                BalanceQuantity = snapshot.BalanceQuantity,
                BalanceEpcs = snapshot.BalanceEpcs,
                LastEpc = snapshot.LastEpc,
                ReaderOnline = snapshot.ReaderOnline,
                StatusMessage = snapshot.StatusMessage,
                ActiveAlarm = snapshot.ActiveAlarm,
                Timestamp = snapshot.Timestamp
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish status for gate {GateCode}", runtime.GateCode);
        }
    }

    private async Task SafeNotifyAsync(Func<Task> publish)
    {
        try
        {
            await publish().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Real-time notification failed");
        }
    }
}
