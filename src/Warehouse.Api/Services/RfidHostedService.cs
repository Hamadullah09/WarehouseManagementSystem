using Microsoft.Extensions.Options;
using Warehouse.Application.Gates;
using Warehouse.Rfid.Abstractions;
using Warehouse.Rfid.Simulation;
using Warehouse.Rfid.U300;

namespace Warehouse.Api.Services;

/// <summary>
/// Builds the configured readers, wires their events to the gate service and
/// owns their lifetime for the process.
/// </summary>
/// <remarks>
/// This is the only place where a concrete driver is chosen. Everything above
/// it sees <see cref="IRfidReader"/>, which is what makes swapping the reader
/// a configuration change rather than a rewrite (§24).
///
/// Startup order matters: gates are loaded and their runtime built before any
/// reader is allowed to connect, so an input edge arriving in the first
/// milliseconds cannot find a gate that does not exist yet.
/// </remarks>
public sealed class RfidHostedService(
    RfidReaderRegistry registry,
    IGateCycleService gates,
    IOptions<RfidOptions> options,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory,
    ILogger<RfidHostedService> logger) : IHostedService
{
    private readonly List<IRfidReader> _readers = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;

        GuardSimulation(config);

        await gates.InitializeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var readerConfig in config.Readers.Where(r => r.Enabled))
        {
            IRfidReader reader;

            switch (readerConfig.Driver)
            {
                case RfidDriverKind.Simulation:
                    reader = new SimulatedRfidReader(
                        readerConfig, loggerFactory.CreateLogger<SimulatedRfidReader>());

                    break;

                case RfidDriverKind.U300:
                    reader = new U300RfidReader(
                        readerConfig, loggerFactory.CreateLogger<U300RfidReader>());

                    break;

                default:
                    logger.LogError(
                        "Reader {ReaderId} has unsupported driver {Driver}; skipping",
                        readerConfig.ReaderId, readerConfig.Driver);

                    continue;
            }

            Wire(reader);
            registry.Register(readerConfig.GateId, reader);
            _readers.Add(reader);

            logger.LogInformation(
                "Registered {Driver} reader {ReaderId} on gate {GateId}",
                readerConfig.Driver, readerConfig.ReaderId, readerConfig.GateId);
        }

        // Connect without blocking startup: an unreachable reader must not
        // stop the API, the dashboard or document entry from coming up.
        foreach (var reader in _readers)
        {
            _ = ConnectAsync(reader);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var reader in _readers)
        {
            try
            {
                await reader.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error shutting down reader {ReaderId}", reader.ReaderId);
            }
        }

        _readers.Clear();
    }

    /// <summary>
    /// Refuses to start with simulated readers outside Development.
    /// </summary>
    /// <remarks>
    /// The brief requires simulation and production to be unmistakably
    /// separate. A misconfigured production host fails loudly at startup
    /// rather than quietly accepting fabricated tag reads as real stock
    /// movement (§40).
    /// </remarks>
    private void GuardSimulation(RfidOptions config)
    {
        var simulated = config.Readers.Where(r => r.Enabled && r.Driver == RfidDriverKind.Simulation).ToList();

        if (simulated.Count == 0 && !config.AllowSimulation)
        {
            return;
        }

        if (environment.IsDevelopment())
        {
            if (simulated.Count > 0)
            {
                logger.LogWarning(
                    "SIMULATION MODE: {Count} reader(s) are simulated ({Readers}). No physical reader is in use.",
                    simulated.Count, string.Join(", ", simulated.Select(r => r.ReaderId)));
            }

            return;
        }

        throw new InvalidOperationException(
            "Simulated RFID readers are enabled outside the Development environment. "
            + "Set Rfid:AllowSimulation to false and give every reader Driver=U300, "
            + "or run this host with ASPNETCORE_ENVIRONMENT=Development.");
    }

    private async Task ConnectAsync(IRfidReader reader)
    {
        try
        {
            var connected = await reader.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation(
                "Reader {ReaderId} initial connection: {Result}",
                reader.ReaderId, connected ? "connected" : "not yet available, retrying in background");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reader {ReaderId} failed to connect", reader.ReaderId);
        }
    }

    private void Wire(IRfidReader reader)
    {
        // Tag reads are synchronous by design: the handler is a dictionary
        // write, and hopping threads for every read would cost more than it
        // saves at 950 tags/sec.
        reader.TagRead += (_, e) => gates.HandleTagRead(e.ReaderId, e.Tag);

        reader.GpiChanged += (_, e) => Detach(
            () => gates.HandleGpiChangedAsync(e.ReaderId, e.State, e.At),
            "GPI change", e.ReaderId);

        reader.StateChanged += (_, e) => Detach(
            () => gates.HandleReaderStateChangedAsync(e.ReaderId, e.Previous, e.Current, e.Reason),
            "state change", e.ReaderId);

        reader.Error += (_, e) => Detach(
            () => gates.HandleReaderErrorAsync(e.ReaderId, e.Operation, e.Message, e.Code),
            "reader error", e.ReaderId);
    }

    /// <summary>
    /// Runs async work off the driver's callback thread. The vendor SDK calls
    /// back on its own receive thread, and blocking it would stall the tag
    /// stream for every gate on that reader.
    /// </summary>
    private void Detach(Func<Task> work, string description, string readerId) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handling {Description} from reader {ReaderId} failed", description, readerId);
            }
        });
}
