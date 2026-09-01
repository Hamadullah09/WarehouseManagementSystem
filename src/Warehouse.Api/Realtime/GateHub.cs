using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Gates;
using Warehouse.Application.Realtime;

namespace Warehouse.Api.Realtime;

/// <summary>
/// Real-time channel for gate displays and the admin dashboard (§22).
/// </summary>
/// <remarks>
/// Displays join a group per gate so a busy warehouse does not fan every read
/// out to every screen. The dashboard joins <c>dashboard</c> and receives
/// everything.
///
/// Nothing about the gate cycle depends on this hub: it is a projection. If
/// every display disconnects, movements are still validated, committed and
/// audited, and the screens catch up from the REST snapshot when they return.
/// </remarks>
[Authorize]
public sealed class GateHub(IGateCycleService gates, ILogger<GateHub> logger) : Hub
{
    public const string DashboardGroup = "dashboard";

    public static string GroupFor(string gateCode) => $"gate:{gateCode}";

    /// <summary>Subscribes this connection to one gate and returns its current snapshot.</summary>
    public async Task<GateSnapshot?> JoinGate(string gateCode)
    {
        if (string.IsNullOrWhiteSpace(gateCode))
        {
            throw new HubException("A gate code is required.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(gateCode));

        logger.LogInformation(
            "Connection {ConnectionId} joined gate {GateCode}", Context.ConnectionId, gateCode);

        return await gates.GetSnapshotAsync(gateCode, Context.ConnectionAborted);
    }

    public Task LeaveGate(string gateCode) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(gateCode));

    /// <summary>Subscribes this connection to every gate.</summary>
    public async Task<IReadOnlyList<GateSnapshot>> JoinDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroup);

        return await gates.GetAllSnapshotsAsync(Context.ConnectionAborted);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            logger.LogDebug(exception, "Connection {ConnectionId} dropped", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// SignalR implementation of <see cref="IGateNotifier"/>.
/// </summary>
/// <remarks>
/// Every method swallows its own failures. A broadcast is a courtesy to the
/// user interface; it must never be able to fail a gate cycle or roll back an
/// inventory transaction.
/// </remarks>
public sealed class SignalRGateNotifier(
    IHubContext<GateHub> hub,
    ILogger<SignalRGateNotifier> logger) : IGateNotifier
{
    public Task GateStatusChangedAsync(GateStatusUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("GateStatusChanged", update.GateCode, update, cancellationToken);

    public Task EpcDetectedAsync(EpcDetectedUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("EpcDetected", update.GateCode, update, cancellationToken);

    public Task CycleCompletedAsync(CycleCompletedUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("CycleCompleted", update.GateCode, update, cancellationToken);

    public Task AlarmRaisedAsync(AlarmRaisedUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("AlarmRaised", update.GateCode, update, cancellationToken);

    public Task ReaderStatusChangedAsync(ReaderStatusUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("ReaderStatusChanged", update.GateCode, update, cancellationToken);

    public Task GpioChangedAsync(GpioStateUpdate update, CancellationToken cancellationToken = default) =>
        PublishAsync("GpioChanged", update.GateCode, update, cancellationToken);

    private async Task PublishAsync<T>(
        string method,
        string? gateCode,
        T payload,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(gateCode))
            {
                await hub.Clients
                    .Group(GateHub.GroupFor(gateCode))
                    .SendAsync(method, payload, cancellationToken)
                    .ConfigureAwait(false);
            }

            await hub.Clients
                .Group(GateHub.DashboardGroup)
                .SendAsync(method, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast {Method} for gate {GateCode}", method, gateCode);
        }
    }
}
