package com.smatechnology.denimrolls.gate;

/**
 * When the gate reads, when it stops, and when it refuses to carry on.
 *
 * <p>Pure: no Android, no reader, no clock. It is the rules of the gate and
 * nothing else, so the rules can be checked at a desk instead of by standing
 * at a barrier feeding rolls past a beam. The same reasoning as
 * {@code Warehouse.Domain.Gates.GateStateMachine} on the server, for the same
 * reason -- the part that must not be wrong is the part with no dependencies.
 *
 * <h3>The gate</h3>
 *
 * A sensor holds a beam across the opening, powered by itself. A roll breaks
 * the beam and 12V appears on an input; the roll clears and the beam closes
 * again. One break, one roll:
 *
 * <pre>
 *   IDLE          no session
 *   FREE_RUNNING  session open, reading continuously; the buttons run it
 *   OPEN          session open, waiting for a roll to break the beam
 *   READING       a roll is in the beam
 *   LATCHED       something was wrong and nobody has said it is dealt with
 * </pre>
 *
 * <p>LATCHED has no way out but {@link #reset()}. It is reached by a roll that
 * is not on the document, or by a roll that broke the beam and answered
 * nothing, and both leave a physical job to do that no timer can do. Beam
 * breaks arriving while latched are refused rather than counted, so a load
 * that keeps coming cannot quietly run past an unresolved fault.
 */
public final class GateCycle {

    public enum State {
        IDLE,
        FREE_RUNNING,
        OPEN,
        READING,
        LATCHED
    }

    /** What the screen and the reader are asked to do about an event. */
    public enum Action {
        NOTHING,
        START_READING,
        STOP_READING,
        JUDGE_ROLL,
        LATCH_WRONG_ROLL,
        LATCH_MISSED_ROLL,
        RESUME_READING,
        RESUME_WAITING
    }

    private State state = State.IDLE;

    /**
     * How this session is being run. LATCHED does not say on its own, and
     * RESET has to put the session back the way it was.
     */
    private boolean gateDriven;

    /** Tags delivered during the current beam break, repeats included. */
    private int tagsThisRoll;

    public State state() {
        return state;
    }

    public boolean isLatched() {
        return state == State.LATCHED;
    }

    public boolean isSessionOpen() {
        return state != State.IDLE;
    }

    public boolean isGateDriven() {
        return state != State.IDLE && gateDriven;
    }

    public int tagsThisRoll() {
        return tagsThisRoll;
    }

    /**
     * START.
     *
     * @param fromGate true where a beam sensor is wired and configured
     */
    public Action start(boolean fromGate) {
        if (state != State.IDLE) {
            return Action.NOTHING;
        }

        gateDriven = fromGate;
        tagsThisRoll = 0;

        if (fromGate) {
            // Deliberately not reading yet. START opens the session; the beam
            // decides when anything is read, and does so for every roll after
            // this without the operator touching the screen again.
            state = State.OPEN;

            return Action.NOTHING;
        }

        state = State.FREE_RUNNING;

        return Action.START_READING;
    }

    /** STOP. Always allowed: an operator can always abandon a load. */
    public Action stop() {
        if (state == State.IDLE) {
            return Action.NOTHING;
        }

        state = State.IDLE;
        tagsThisRoll = 0;

        return Action.STOP_READING;
    }

    /** 12V appeared: a roll is in the beam. */
    public Action beamBroken() {
        if (state != State.OPEN) {
            // Already READING, or LATCHED and refusing, or no session at all.
            return Action.NOTHING;
        }

        state = State.READING;
        tagsThisRoll = 0;

        return Action.START_READING;
    }

    /**
     * The beam closed: the roll has cleared.
     *
     * <p>Two steps, because tags queued in the reader are still arriving as
     * the signal drops. The caller stops the reader on {@link Action#JUDGE_ROLL},
     * lets those land, and then calls {@link #judge()}.
     */
    public Action beamRestored() {
        return state == State.READING ? Action.JUDGE_ROLL : Action.NOTHING;
    }

    /** The verdict on the roll that just cleared the beam. */
    public Action judge() {
        if (state != State.READING) {
            return Action.NOTHING;
        }

        if (tagsThisRoll == 0) {
            state = State.LATCHED;

            return Action.LATCH_MISSED_ROLL;
        }

        state = State.OPEN;

        return Action.NOTHING;
    }

    /** A tag was delivered. Counted whether or not it is on the document. */
    public void tagSeen() {
        tagsThisRoll++;
    }

    /**
     * The tag just read is not on this document.
     *
     * <p>Latches wherever a session is open, gate or buttons: the roll is
     * wrong in both, and in both it has to come off before anything else
     * happens.
     */
    public Action wrongRoll() {
        if (state == State.IDLE || state == State.LATCHED) {
            return Action.NOTHING;
        }

        state = State.LATCHED;

        return Action.LATCH_WRONG_ROLL;
    }

    /** RESET: somebody has dealt with it. */
    public Action reset() {
        if (state != State.LATCHED) {
            return Action.NOTHING;
        }

        tagsThisRoll = 0;

        if (gateDriven) {
            state = State.OPEN;

            return Action.RESUME_WAITING;
        }

        state = State.FREE_RUNNING;

        return Action.RESUME_READING;
    }
}
