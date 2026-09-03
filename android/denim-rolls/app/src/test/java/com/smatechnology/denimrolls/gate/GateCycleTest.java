package com.smatechnology.denimrolls.gate;

import static com.smatechnology.denimrolls.gate.GateCycle.Action.JUDGE_ROLL;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.LATCH_MISSED_ROLL;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.LATCH_WRONG_ROLL;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.NOTHING;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.RESUME_READING;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.RESUME_WAITING;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.START_READING;
import static com.smatechnology.denimrolls.gate.GateCycle.Action.STOP_READING;
import static com.smatechnology.denimrolls.gate.GateCycle.State.IDLE;
import static com.smatechnology.denimrolls.gate.GateCycle.State.LATCHED;
import static com.smatechnology.denimrolls.gate.GateCycle.State.OPEN;
import static com.smatechnology.denimrolls.gate.GateCycle.State.READING;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

/**
 * The gate, exercised the way rolls actually arrive.
 *
 * <p>Written so the behaviour can be confirmed away from the hardware. Every
 * test below is a sequence somebody could act out at the barrier, and the
 * awkward ones -- a load that keeps coming while an alarm is up, a beam that
 * chatters -- are the ones worth having.
 */
public final class GateCycleTest {

    private final GateCycle gate = new GateCycle();

    // ------------------------------------------------------------- the loop

    @Test
    public void startFromTheGateDoesNotReadYet() {
        assertEquals(NOTHING, gate.start(true));
        assertEquals(OPEN, gate.state());
    }

    @Test
    public void startFromTheButtonsReadsImmediately() {
        assertEquals(START_READING, gate.start(false));
    }

    @Test
    public void aGoodRollReadsAndReturnsToWaiting() {
        gate.start(true);

        assertEquals(START_READING, gate.beamBroken());
        assertEquals(READING, gate.state());

        gate.tagSeen();

        assertEquals(JUDGE_ROLL, gate.beamRestored());
        assertEquals(NOTHING, gate.judge());
        assertEquals(OPEN, gate.state());
    }

    @Test
    public void everyRollAfterTheFirstNeedsNoFurtherInput() {
        gate.start(true);

        for (int roll = 0; roll < 30; roll++) {
            assertEquals(START_READING, gate.beamBroken());
            gate.tagSeen();
            assertEquals(JUDGE_ROLL, gate.beamRestored());
            assertEquals(NOTHING, gate.judge());
        }

        assertEquals(OPEN, gate.state());
        assertFalse(gate.isLatched());
    }

    // -------------------------------------------------------------- latches

    @Test
    public void aRollThatReadsNothingLatches() {
        gate.start(true);
        gate.beamBroken();

        assertEquals(JUDGE_ROLL, gate.beamRestored());
        assertEquals(LATCH_MISSED_ROLL, gate.judge());
        assertTrue(gate.isLatched());
    }

    @Test
    public void aRollNotOnTheDocumentLatches() {
        gate.start(true);
        gate.beamBroken();
        gate.tagSeen();

        assertEquals(LATCH_WRONG_ROLL, gate.wrongRoll());
        assertTrue(gate.isLatched());
    }

    @Test
    public void aWrongRollLatchesFromTheButtonsToo() {
        gate.start(false);
        gate.tagSeen();

        assertEquals(LATCH_WRONG_ROLL, gate.wrongRoll());
    }

    /** The load keeps coming. None of it may be counted until somebody looks. */
    @Test
    public void rollsArrivingWhileLatchedAreRefused() {
        gate.start(true);
        gate.beamBroken();
        gate.beamRestored();
        gate.judge();

        for (int roll = 0; roll < 5; roll++) {
            assertEquals(NOTHING, gate.beamBroken());
            assertEquals(NOTHING, gate.beamRestored());
            assertEquals(LATCHED, gate.state());
        }
    }

    @Test
    public void aSecondFaultCannotStackOnALatch() {
        gate.start(true);
        gate.beamBroken();
        gate.beamRestored();
        gate.judge();

        assertEquals(NOTHING, gate.wrongRoll());
        assertEquals(LATCHED, gate.state());
    }

    // ---------------------------------------------------------------- reset

    @Test
    public void resetFromTheGateGoesBackToWaiting() {
        gate.start(true);
        gate.beamBroken();
        gate.beamRestored();
        gate.judge();

        assertEquals(RESUME_WAITING, gate.reset());
        assertEquals(OPEN, gate.state());

        // and the gate works again
        assertEquals(START_READING, gate.beamBroken());
    }

    @Test
    public void resetFromTheButtonsGoesBackToReading() {
        gate.start(false);
        gate.tagSeen();
        gate.wrongRoll();

        assertEquals(RESUME_READING, gate.reset());
    }

    @Test
    public void resetDoesNothingWhenNothingIsLatched() {
        gate.start(true);

        assertEquals(NOTHING, gate.reset());
        assertEquals(OPEN, gate.state());
    }

    @Test
    public void resetClearsTheRollCountSoTheNextRollIsJudgedOnItsOwn() {
        gate.start(true);
        gate.beamBroken();
        gate.tagSeen();
        gate.wrongRoll();
        gate.reset();

        gate.beamBroken();
        assertEquals(0, gate.tagsThisRoll());
        assertEquals(LATCH_MISSED_ROLL, next(gate));
    }

    // ------------------------------------------------------------- the edges

    @Test
    public void aBeamThatChattersDoesNotStartTwoRolls() {
        gate.start(true);

        assertEquals(START_READING, gate.beamBroken());
        assertEquals(NOTHING, gate.beamBroken());
        assertEquals(READING, gate.state());
    }

    @Test
    public void aBeamRestoredWithNoBreakBeforeItIsIgnored() {
        gate.start(true);

        assertEquals(NOTHING, gate.beamRestored());
        assertEquals(OPEN, gate.state());
    }

    @Test
    public void theGateIsDeadUntilSomebodyPressesStart() {
        assertEquals(NOTHING, gate.beamBroken());
        assertEquals(NOTHING, gate.beamRestored());
        assertEquals(NOTHING, gate.wrongRoll());
        assertEquals(IDLE, gate.state());
    }

    @Test
    public void stopEndsTheSessionFromAnywhere() {
        gate.start(true);
        gate.beamBroken();
        gate.beamRestored();
        gate.judge();

        assertEquals(LATCHED, gate.state());
        assertEquals(STOP_READING, gate.stop());
        assertEquals(IDLE, gate.state());

        // and the gate is dead again
        assertEquals(NOTHING, gate.beamBroken());
    }

    @Test
    public void startIsIgnoredWhileASessionIsAlreadyOpen() {
        gate.start(true);

        assertEquals(NOTHING, gate.start(false));
        assertEquals(OPEN, gate.state());
    }

    @Test
    public void repeatsOfTheSameTagStillCountAsAnswering() {
        gate.start(true);
        gate.beamBroken();

        // A roll sent through twice has a working tag either way; the cycle
        // asks whether anything answered, not whether it was new.
        gate.tagSeen();
        gate.tagSeen();

        gate.beamRestored();
        assertEquals(NOTHING, gate.judge());
    }

    private static GateCycle.Action next(GateCycle gate) {
        gate.beamRestored();

        return gate.judge();
    }
}
