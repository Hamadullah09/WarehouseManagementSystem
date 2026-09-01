using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Gates;
using Warehouse.Application.Options;
using Warehouse.Application.Realtime;
using Warehouse.Domain;
using Warehouse.Domain.Entities;
using Warehouse.Domain.Validation;
using Warehouse.Infrastructure;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Rfid.Abstractions;
using Warehouse.Rfid.Simulation;

namespace Warehouse.Application.Tests;

/// <summary>Captures everything that would have been broadcast, for assertions.</summary>
public sealed class RecordingNotifier : IGateNotifier
{
    public List<GateStatusUpdate> GateStatus { get; } = [];

    public List<EpcDetectedUpdate> EpcDetected { get; } = [];

    public List<CycleCompletedUpdate> CycleCompleted { get; } = [];

    public List<AlarmRaisedUpdate> Alarms { get; } = [];

    public List<ReaderStatusUpdate> ReaderStatus { get; } = [];

    public List<GpioStateUpdate> Gpio { get; } = [];

    private readonly Lock _sync = new();

    public Task GateStatusChangedAsync(GateStatusUpdate u, CancellationToken ct = default) => Add(GateStatus, u);

    public Task EpcDetectedAsync(EpcDetectedUpdate u, CancellationToken ct = default) => Add(EpcDetected, u);

    public Task CycleCompletedAsync(CycleCompletedUpdate u, CancellationToken ct = default) => Add(CycleCompleted, u);

    public Task AlarmRaisedAsync(AlarmRaisedUpdate u, CancellationToken ct = default) => Add(Alarms, u);

    public Task ReaderStatusChangedAsync(ReaderStatusUpdate u, CancellationToken ct = default) => Add(ReaderStatus, u);

    public Task GpioChangedAsync(GpioStateUpdate u, CancellationToken ct = default) => Add(Gpio, u);

    private Task Add<T>(List<T> target, T item)
    {
        lock (_sync)
        {
            target.Add(item);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Test caller identity.</summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public int? UserId { get; set; } = 1;

    public string? UserName { get; set; } = "tester";

    public string? IpAddress => "127.0.0.1";

    public HashSet<string> Roles { get; } = [.. RoleNames.All];

    public bool IsInRole(string role) => Roles.Contains(role);
}

/// <summary>
/// A whole warehouse in one process: in-memory database, real services, and a
/// simulated reader wired exactly as the production host wires a U300.
/// </summary>
/// <remarks>
/// The point is that nothing between the reader adapter and the database is
/// faked. Gate cycles run through the real <see cref="GateCycleService"/>,
/// the real validation engine and the real inventory transaction, so these
/// tests exercise the behaviour the brief's Definition of Done describes
/// rather than a stand-in for it (§48).
/// </remarks>
public sealed class WarehouseTestHost : IAsyncDisposable
{
    public ServiceProvider Services { get; }

    public RecordingNotifier Notifier { get; } = new();

    public TestCurrentUser CurrentUser { get; } = new();

    public RfidReaderRegistry Registry { get; } = new();

    public SimulatedRfidReader Reader { get; }

    public IGateCycleService Gates { get; }

    public const string GateCode = "GATE-01";

    public const string ReaderId = "SIM-TEST";

    public WarehouseTestHost(Action<GateOptions>? configureGate = null)
    {
        var gateOptions = new GateOptions
        {
            // No drain and no minimum interval by default: tests drive events
            // deterministically and should not pay wall-clock time for it.
            DrainMs = 0,
            MinimumCycleIntervalMs = 0,
            CycleTimeoutMs = 30_000,
            AutoRearmAfterPass = true,
            AutoRearmAfterAlarm = true
        };

        configureGate?.Invoke(gateOptions);

        var readerOptions = new RfidReaderOptions
        {
            ReaderId = ReaderId,
            Name = "Simulated reader",
            GateId = GateCode,
            Driver = RfidDriverKind.Simulation,
            Antennas = [1]
        };

        var rfidOptions = new RfidOptions { AllowSimulation = true, Readers = [readerOptions] };

        var services = new ServiceCollection();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Named once, outside the lambda: the lambda runs per context
        // creation, so generating the name inside it would give every scope
        // its own empty database.
        var databaseName = $"warehouse-{Guid.NewGuid():N}";

        services.AddDbContext<WarehouseDbContext>(o => o
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<IWarehouseDbContext>(sp => sp.GetRequiredService<WarehouseDbContext>());
        services.AddWarehouseServices();

        services.AddSingleton<ICurrentUser>(CurrentUser);
        services.AddSingleton<IGateNotifier>(Notifier);
        services.AddSingleton<IValidationEngine, ValidationEngine>();
        services.AddSingleton<IRfidReaderRegistry>(Registry);
        services.AddSingleton(Registry);
        services.AddSingleton<IGateIndicator, GateIndicator>();

        services.AddSingleton<IOptionsMonitor<GateOptions>>(new StaticOptions<GateOptions>(gateOptions));
        services.AddSingleton<IOptionsMonitor<DocumentOptions>>(new StaticOptions<DocumentOptions>(new DocumentOptions()));
        services.AddSingleton<IOptionsMonitor<AlarmOptions>>(
            new StaticOptions<AlarmOptions>(new AlarmOptions { RequireSupervisorToResolve = false }));
        services.AddSingleton<IOptionsMonitor<RfidOptions>>(new StaticOptions<RfidOptions>(rfidOptions));

        services.AddSingleton<IGateCycleService, GateCycleService>();

        Services = services.BuildServiceProvider();

        Reader = new SimulatedRfidReader(
            readerOptions, Services.GetRequiredService<ILoggerFactory>().CreateLogger<SimulatedRfidReader>());

        Gates = Services.GetRequiredService<IGateCycleService>();
    }

    /// <summary>Creates the gate, the reader row and the requested EPC catalogue.</summary>
    public async Task<WarehouseTestHost> StartAsync(IEnumerable<string>? epcs = null, EpcStatus status = EpcStatus.Registered)
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();

            foreach (var name in RoleNames.All)
            {
                db.Roles.Add(new Role { Name = name });
            }

            db.Users.Add(new User
            {
                Id = 1,
                UserName = "tester",
                DisplayName = "Test Operator",
                PasswordHash = "not-used",
                CreatedAt = DateTimeOffset.UtcNow
            });

            var gate = new Gate
            {
                Code = GateCode,
                Name = "Main gate",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.Gates.Add(gate);
            await db.SaveChangesAsync();

            db.Readers.Add(new Reader
            {
                ReaderId = ReaderId,
                Name = "Simulated reader",
                GateId = gate.Id,
                Model = "Simulator",
                IsActive = true,
                IsOnline = true
            });

            foreach (var epc in epcs ?? [])
            {
                db.EpcTags.Add(new EpcTag
                {
                    Epc = Epc.Normalize(epc),
                    UnitQuantity = 4,
                    Status = status,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            await db.SaveChangesAsync();
        }

        await Gates.InitializeAsync();

        // Wire the reader exactly as RfidHostedService does in production.
        Reader.TagRead += (_, e) => Gates.HandleTagRead(e.ReaderId, e.Tag);
        Reader.GpiChanged += (_, e) => Gates.HandleGpiChangedAsync(e.ReaderId, e.State, e.At).GetAwaiter().GetResult();
        Reader.StateChanged += (_, e) =>
            Gates.HandleReaderStateChangedAsync(e.ReaderId, e.Previous, e.Current, e.Reason).GetAwaiter().GetResult();
        Reader.Error += (_, e) =>
            Gates.HandleReaderErrorAsync(e.ReaderId, e.Operation, e.Message, e.Code).GetAwaiter().GetResult();

        Registry.Register(GateCode, Reader);

        await Reader.ConnectAsync();

        return this;
    }

    /// <summary>Runs one whole gate pass: input active, tags, input clear.</summary>
    public async Task RunCycleAsync(IEnumerable<string> epcs, int repeats = 3)
    {
        await Reader.GpioOnAsync();
        await Reader.EmitTagsAsync(epcs, repeats);
        await Reader.GpioOffAsync();
    }

    public IServiceScope Scope() => Services.CreateScope();

    public async Task<T> WithDbAsync<T>(Func<WarehouseDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<WarehouseDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        await Services.DisposeAsync();
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
