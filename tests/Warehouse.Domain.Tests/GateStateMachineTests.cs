using FluentAssertions;
using Warehouse.Domain;
using Warehouse.Domain.Gates;
using Xunit;

namespace Warehouse.Domain.Tests;

/// <summary>
/// Guards the gate lifecycle (§35). The interesting assertions are the
/// negative ones: a state machine earns its place by making bad transitions
/// impossible, not by allowing the good ones.
/// </summary>
public class GateStateMachineTests
{
    [Fact]
    public void Happy_path_runs_idle_to_passed()
    {
        var state = GateState.Idle;

        state = Fire(state, GateTrigger.AssignDocument);
        state.Should().Be(GateState.Ready);

        state = Fire(state, GateTrigger.Arm);
        state.Should().Be(GateState.WaitingForGate);

        state = Fire(state, GateTrigger.GateSignalOn);
        state.Should().Be(GateState.Reading);

        state = Fire(state, GateTrigger.GateSignalOff);
        state.Should().Be(GateState.Processing);

        state = Fire(state, GateTrigger.BeginValidation);
        state.Should().Be(GateState.Validating);

        state = Fire(state, GateTrigger.ValidationPassed);
        state.Should().Be(GateState.Passed);
    }

    [Fact]
    public void Failed_validation_lands_in_alarm_and_needs_acknowledgement()
    {
        var state = GateState.Validating;

        state = Fire(state, GateTrigger.ValidationFailed);
        state.Should().Be(GateState.Alarm);

        // An alarmed gate cannot simply be armed again; someone must clear it.
        GateStateMachine.CanFire(state, GateTrigger.Arm).Should().BeFalse();

        state = Fire(state, GateTrigger.AcknowledgeAlarm);
        state.Should().Be(GateState.Ready);
    }

    [Fact]
    public void A_second_start_signal_during_a_cycle_is_rejected()
    {
        // This is what makes a bouncing sensor produce one cycle, not two (§28).
        GateStateMachine.CanFire(GateState.Reading, GateTrigger.GateSignalOn).Should().BeFalse();
        GateStateMachine.CanFire(GateState.Processing, GateTrigger.GateSignalOn).Should().BeFalse();
        GateStateMachine.CanFire(GateState.Validating, GateTrigger.GateSignalOn).Should().BeFalse();
    }

    [Fact]
    public void A_cycle_cannot_start_while_the_reader_is_offline()
    {
        GateStateMachine.CanStartCycle(GateState.ReaderDisconnected).Should().BeFalse();
        GateStateMachine.CanFire(GateState.ReaderDisconnected, GateTrigger.GateSignalOn).Should().BeFalse();
    }

    [Fact]
    public void Only_an_armed_gate_can_start_a_cycle()
    {
        foreach (var state in Enum.GetValues<GateState>())
        {
            GateStateMachine.CanStartCycle(state).Should().Be(state == GateState.WaitingForGate);
        }
    }

    [Theory]
    [InlineData(GateState.Idle)]
    [InlineData(GateState.Ready)]
    [InlineData(GateState.WaitingForGate)]
    [InlineData(GateState.Reading)]
    [InlineData(GateState.Processing)]
    [InlineData(GateState.Validating)]
    [InlineData(GateState.Passed)]
    [InlineData(GateState.Alarm)]
    public void Reader_loss_interrupts_any_state(GateState from)
    {
        Fire(from, GateTrigger.ReaderLost).Should().Be(GateState.ReaderDisconnected);
    }

    [Fact]
    public void Reader_recovery_returns_the_gate_to_service()
    {
        Fire(GateState.ReaderDisconnected, GateTrigger.ReaderRestored).Should().Be(GateState.Idle);
    }

    [Fact]
    public void A_stuck_input_times_out_into_processing_rather_than_hanging()
    {
        Fire(GateState.Reading, GateTrigger.Timeout).Should().Be(GateState.Processing);
    }

    [Fact]
    public void Cycle_is_active_only_while_reads_can_still_arrive()
    {
        GateStateMachine.IsCycleActive(GateState.Reading).Should().BeTrue();
        GateStateMachine.IsCycleActive(GateState.Processing).Should().BeTrue();
        GateStateMachine.IsCycleActive(GateState.Validating).Should().BeTrue();

        GateStateMachine.IsCycleActive(GateState.WaitingForGate).Should().BeFalse();
        GateStateMachine.IsCycleActive(GateState.Passed).Should().BeFalse();
        GateStateMachine.IsCycleActive(GateState.Alarm).Should().BeFalse();
        GateStateMachine.IsCycleActive(GateState.Idle).Should().BeFalse();
    }

    [Fact]
    public void Every_non_idle_state_has_a_way_back_to_service()
    {
        // A gate that can get stuck with no exit is a gate that stops the
        // warehouse, so this is checked exhaustively rather than by example.
        foreach (var state in Enum.GetValues<GateState>().Where(s => s != GateState.Idle))
        {
            GateStateMachine.AllowedTriggers(state).Should()
                .NotBeEmpty($"state {state} must offer at least one transition");
        }
    }

    [Fact]
    public void Transition_table_has_no_duplicate_entries()
    {
        var duplicates = GateStateMachine.All
            .GroupBy(t => (t.From, t.Trigger))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty("an ambiguous transition would make the next state undefined");
    }

    private static GateState Fire(GateState from, GateTrigger trigger)
    {
        var next = GateStateMachine.Next(from, trigger);
        next.Should().NotBeNull($"{trigger} should be legal from {from}");

        return next!.Value;
    }
}

public class EpcTests
{
    [Theory]
    [InlineData("e280 1160 6000", "E28011606000")]
    [InlineData("  abcdef  ", "ABCDEF")]
    [InlineData("ABCDEF", "ABCDEF")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_uppercases_and_strips_whitespace(string? input, string expected)
    {
        Epc.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("E2801160", true)]
    [InlineData("e2801160", true)]
    [InlineData("E280116", false)]   // odd length
    [InlineData("E28011GG", false)]  // not hex
    [InlineData("", false)]
    public void IsValid_accepts_only_even_length_hex(string input, bool expected)
    {
        Epc.IsValid(input).Should().Be(expected);
    }

    [Fact]
    public void Normalisation_makes_reader_casing_irrelevant()
    {
        // A reader reporting lower case must never look like an unknown tag.
        Epc.Normalize("e2801160a2000001").Should().Be(Epc.Normalize("E2801160A2000001"));
    }
}
