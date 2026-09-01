using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Rfid.Abstractions;

namespace Warehouse.Application.Gates;

/// <summary>Drives the physical pass/alarm indicators wired to the reader outputs.</summary>
public interface IGateIndicator
{
    /// <summary>Pulses the alarm output for the configured duration.</summary>
    Task SignalAlarmAsync(string gateCode, CancellationToken cancellationToken = default);

    /// <summary>Pulses the pass output for the configured duration.</summary>
    Task SignalPassAsync(string gateCode, CancellationToken cancellationToken = default);

    /// <summary>Drops both outputs immediately.</summary>
    Task ClearAsync(string gateCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Beacon and sounder control over the U300 optocoupler outputs.
/// </summary>
/// <remarks>
/// The outputs are open-collector: asserting one conducts IO_GND through the
/// output pin, which is what sinks a relay coil or lamp return. Which physical
/// line is used comes from configuration, never from source.
///
/// Pulses run detached on a timer rather than blocking the caller, because the
/// gate cycle must not wait on a beacon. Every failure is logged and
/// swallowed: an indicator that will not light is a maintenance problem, not a
/// reason to fail a warehouse movement that already happened.
/// </remarks>
public sealed class GateIndicator(
    IRfidReaderRegistry registry,
    IOptionsMonitor<RfidOptions> options,
    ILogger<GateIndicator> logger) : IGateIndicator
{
    public Task SignalAlarmAsync(string gateCode, CancellationToken cancellationToken = default)
    {
        var cfg = Find(gateCode);
        return cfg?.Gpio.AlarmOutput is { } pin
            ? PulseAsync(gateCode, pin, cfg.Gpio.AlarmPulseMs, cancellationToken)
            : Task.CompletedTask;
    }

    public Task SignalPassAsync(string gateCode, CancellationToken cancellationToken = default)
    {
        var cfg = Find(gateCode);
        return cfg?.Gpio.PassOutput is { } pin
            ? PulseAsync(gateCode, pin, cfg.Gpio.PassPulseMs, cancellationToken)
            : Task.CompletedTask;
    }

    public async Task ClearAsync(string gateCode, CancellationToken cancellationToken = default)
    {
        var cfg = Find(gateCode);
        var reader = registry.ForGate(gateCode);

        if (cfg is null || reader is null)
        {
            return;
        }

        var commands = new List<GpoCommand>(2);

        if (cfg.Gpio.AlarmOutput is { } alarm)
        {
            commands.Add(new GpoCommand(alarm, false));
        }

        if (cfg.Gpio.PassOutput is { } pass)
        {
            commands.Add(new GpoCommand(pass, false));
        }

        if (commands.Count == 0)
        {
            return;
        }

        try
        {
            await reader.SetGpioOutputAsync(commands, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear indicator outputs on gate {GateCode}", gateCode);
        }
    }

    private async Task PulseAsync(string gateCode, GpoPin pin, int durationMs, CancellationToken cancellationToken)
    {
        var reader = registry.ForGate(gateCode);

        if (reader is null)
        {
            return;
        }

        try
        {
            await reader.SetGpioOutputAsync([new GpoCommand(pin, true)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to assert {Pin} on gate {GateCode}", pin, gateCode);
            return;
        }

        if (durationMs <= 0)
        {
            return;
        }

        // Detached: the release must still happen if the request that raised
        // the alarm has already completed and its token was cancelled.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, CancellationToken.None).ConfigureAwait(false);
                await reader.SetGpioOutputAsync([new GpoCommand(pin, false)], CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release {Pin} on gate {GateCode}", pin, gateCode);
            }
        }, CancellationToken.None);
    }

    private RfidReaderOptions? Find(string gateCode) =>
        options.CurrentValue.Readers.FirstOrDefault(
            r => string.Equals(r.GateId, gateCode, StringComparison.OrdinalIgnoreCase));
}
