namespace Warehouse.Domain.Gates;

/// <summary>Events that can move a gate between states.</summary>
public enum GateTrigger
{
    /// <summary>A released document was bound to the gate.</summary>
    AssignDocument = 0,

    /// <summary>The document was unbound (cancelled, completed, reassigned).</summary>
    ReleaseDocument = 1,

    /// <summary>Operator or scheduler armed the gate to await the input signal.</summary>
    Arm = 2,

    /// <summary>Gate input went active (12V present).</summary>
    GateSignalOn = 3,

    /// <summary>Gate input cleared.</summary>
    GateSignalOff = 4,

    /// <summary>In-flight reads drained; validation may begin.</summary>
    BeginValidation = 5,

    ValidationPassed = 6,

    ValidationFailed = 7,

    /// <summary>Operator acknowledged the alarm and cleared the gate.</summary>
    AcknowledgeAlarm = 8,

    /// <summary>Reader went offline.</summary>
    ReaderLost = 9,

    /// <summary>Reader came back and verified healthy.</summary>
    ReaderRestored = 10,

    /// <summary>Non-RFID fault.</summary>
    Fault = 11,

    /// <summary>Cycle exceeded its configured maximum duration.</summary>
    Timeout = 12,

    /// <summary>Return an errored or finished gate to service.</summary>
    Reset = 13
}

public readonly record struct GateTransition(GateState From, GateTrigger Trigger, GateState To);

/// <summary>
/// The gate lifecycle as an explicit transition table (§35).
/// </summary>
/// <remarks>
/// Centralising this has one purpose: an illegal move is impossible to express.
/// A second <see cref="GateTrigger.GateSignalOn"/> while already Reading is not
/// a silently-ignored duplicate — it is rejected, and the caller raises a
/// DuplicateGateEvent alarm rather than opening a second cycle.
///
/// Pure and allocation-free on the hot path, so it can be exercised
/// exhaustively in tests without any infrastructure.
/// </remarks>
public static class GateStateMachine
{
    private static readonly GateTransition[] Transitions =
    [
        // Getting a gate into service.
        new(GateState.Idle, GateTrigger.AssignDocument, GateState.Ready),
        new(GateState.Ready, GateTrigger.ReleaseDocument, GateState.Idle),
        new(GateState.Ready, GateTrigger.Arm, GateState.WaitingForGate),
        new(GateState.WaitingForGate, GateTrigger.ReleaseDocument, GateState.Idle),
        new(GateState.WaitingForGate, GateTrigger.Reset, GateState.Ready),

        // Arming an already-armed gate is a no-op, not an error. An operator
        // pressing the button twice, or an auto-rearm racing a manual one,
        // must not fault a gate that is already doing the right thing.
        new(GateState.WaitingForGate, GateTrigger.Arm, GateState.WaitingForGate),

        // The cycle proper: signal on, read, signal off, validate.
        new(GateState.WaitingForGate, GateTrigger.GateSignalOn, GateState.Reading),
        new(GateState.Reading, GateTrigger.GateSignalOff, GateState.Processing),
        new(GateState.Reading, GateTrigger.Timeout, GateState.Processing),
        new(GateState.Processing, GateTrigger.BeginValidation, GateState.Validating),
        new(GateState.Validating, GateTrigger.ValidationPassed, GateState.Passed),
        new(GateState.Validating, GateTrigger.ValidationFailed, GateState.Alarm),

        // After a verdict.
        new(GateState.Passed, GateTrigger.Arm, GateState.WaitingForGate),
        new(GateState.Passed, GateTrigger.Reset, GateState.Ready),
        new(GateState.Passed, GateTrigger.ReleaseDocument, GateState.Idle),
        new(GateState.Alarm, GateTrigger.AcknowledgeAlarm, GateState.Ready),
        new(GateState.Alarm, GateTrigger.ReleaseDocument, GateState.Idle),

        // Reader loss can interrupt anything, and is recoverable.
        new(GateState.Idle, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Ready, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.WaitingForGate, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Reading, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Processing, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Validating, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Passed, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.Alarm, GateTrigger.ReaderLost, GateState.ReaderDisconnected),
        new(GateState.ReaderDisconnected, GateTrigger.ReaderRestored, GateState.Idle),
        new(GateState.ReaderDisconnected, GateTrigger.Reset, GateState.Idle),

        // Faults, and the way back out.
        new(GateState.Ready, GateTrigger.Fault, GateState.Error),
        new(GateState.WaitingForGate, GateTrigger.Fault, GateState.Error),
        new(GateState.Reading, GateTrigger.Fault, GateState.Error),
        new(GateState.Processing, GateTrigger.Fault, GateState.Error),
        new(GateState.Validating, GateTrigger.Fault, GateState.Error),
        new(GateState.Alarm, GateTrigger.Fault, GateState.Error),
        new(GateState.Error, GateTrigger.Reset, GateState.Idle),
        new(GateState.Error, GateTrigger.ReaderRestored, GateState.Idle)
    ];

    /// <summary>Resolves the next state, or null when the move is not legal.</summary>
    public static GateState? Next(GateState from, GateTrigger trigger)
    {
        foreach (var t in Transitions)
        {
            if (t.From == from && t.Trigger == trigger)
            {
                return t.To;
            }
        }

        return null;
    }

    public static bool CanFire(GateState from, GateTrigger trigger) => Next(from, trigger) is not null;

    /// <summary>Triggers that are legal from a given state. Used by the admin UI.</summary>
    public static IReadOnlyList<GateTrigger> AllowedTriggers(GateState from)
    {
        var result = new List<GateTrigger>();

        foreach (var t in Transitions)
        {
            if (t.From == from && !result.Contains(t.Trigger))
            {
                result.Add(t.Trigger);
            }
        }

        return result;
    }

    /// <summary>True while a gate cycle is live and EPCs may still arrive.</summary>
    public static bool IsCycleActive(GateState state) =>
        state is GateState.Reading or GateState.Processing or GateState.Validating;

    /// <summary>
    /// True when the gate is fit to open a new cycle. Explicitly false while a
    /// reader is offline, which is what stops a transaction completing on a
    /// dead reader (§29).
    /// </summary>
    public static bool CanStartCycle(GateState state) => state == GateState.WaitingForGate;

    public static IReadOnlyList<GateTransition> All => Transitions;
}
