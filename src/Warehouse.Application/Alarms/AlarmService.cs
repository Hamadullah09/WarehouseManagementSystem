using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Gates;
using Warehouse.Application.Options;
using Warehouse.Application.Realtime;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Alarms;

/// <summary>Everything needed to raise one alarm.</summary>
public sealed record RaiseAlarmRequest
{
    public required AlarmType AlarmType { get; init; }

    public required string Message { get; init; }

    public int? GateId { get; init; }

    public string? GateCode { get; init; }

    public int? DocumentId { get; init; }

    public string? DocumentNumber { get; init; }

    public long? GateCycleId { get; init; }

    public string? CycleId { get; init; }

    public int? ReaderId { get; init; }

    public string? Epc { get; init; }

    public IReadOnlyList<string> Epcs { get; init; } = [];

    /// <summary>
    /// When true the alarm row is added to the caller's change tracker but not
    /// saved, so it commits with the surrounding transaction.
    /// </summary>
    public bool EnlistOnly { get; init; }
}

public interface IAlarmService
{
    Task<Alarm> RaiseAsync(RaiseAlarmRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Alarm>> RaiseManyAsync(
        IEnumerable<RaiseAlarmRequest> requests,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAsync(long alarmId, CancellationToken cancellationToken = default);

    Task<bool> ResolveAsync(long alarmId, string? notes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Alarm>> GetActiveAsync(int? gateId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raises, tracks and resolves operational alarms (§18).
/// </summary>
/// <remarks>
/// No alarm is ever swallowed: an unknown EPC at a gate is a security event
/// and must leave a durable record with the offending tag attached. Side
/// effects (beacon, real-time push) are best-effort and never allowed to
/// prevent the alarm being persisted.
/// </remarks>
public sealed class AlarmService(
    IWarehouseDbContext db,
    IClock clock,
    ICurrentUser currentUser,
    INumberGenerator numbers,
    IAuditService audit,
    IGateNotifier notifier,
    IGateIndicator indicator,
    IOptionsMonitor<AlarmOptions> options,
    ILogger<AlarmService> logger) : IAlarmService
{
    public async Task<Alarm> RaiseAsync(RaiseAlarmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var alarm = await BuildAsync(request, cancellationToken).ConfigureAwait(false);
        db.Alarms.Add(alarm);

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.AlarmTriggered,
            GateId = request.GateId,
            DocumentId = request.DocumentId,
            DocumentNumber = request.DocumentNumber,
            GateCycleId = request.GateCycleId,
            CycleId = request.CycleId,
            ReaderId = request.ReaderId,
            Epc = request.Epc,
            NewState = AlarmStatus.Active.ToString(),
            Result = request.AlarmType.ToString(),
            Details = request.Message
        });

        if (!request.EnlistOnly)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await AnnounceAsync(alarm, request, cancellationToken).ConfigureAwait(false);
        }

        logger.LogWarning(
            "Alarm {AlarmType} raised on gate {GateCode} cycle {CycleId}: {Message}",
            request.AlarmType, request.GateCode, request.CycleId, request.Message);

        return alarm;
    }

    public async Task<IReadOnlyList<Alarm>> RaiseManyAsync(
        IEnumerable<RaiseAlarmRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var list = requests.ToList();
        var created = new List<Alarm>(list.Count);

        foreach (var request in list)
        {
            created.Add(await RaiseAsync(request with { EnlistOnly = true }, cancellationToken).ConfigureAwait(false));
        }

        if (created.Count == 0)
        {
            return created;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < created.Count; i++)
        {
            await AnnounceAsync(created[i], list[i], cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    public async Task<bool> AcknowledgeAsync(long alarmId, CancellationToken cancellationToken = default)
    {
        var alarm = await db.Alarms.FirstOrDefaultAsync(a => a.Id == alarmId, cancellationToken)
            .ConfigureAwait(false);

        if (alarm is null || alarm.Status != AlarmStatus.Active)
        {
            return false;
        }

        alarm.Status = AlarmStatus.Acknowledged;
        alarm.AcknowledgedByUserId = currentUser.UserId;
        alarm.AcknowledgedAt = clock.UtcNow;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.AlarmAcknowledged,
            GateId = alarm.GateId,
            DocumentId = alarm.DocumentId,
            GateCycleId = alarm.GateCycleId,
            Epc = alarm.Epc,
            PreviousState = AlarmStatus.Active.ToString(),
            NewState = AlarmStatus.Acknowledged.ToString(),
            Details = alarm.AlarmId
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ResolveAsync(long alarmId, string? notes, CancellationToken cancellationToken = default)
    {
        if (options.CurrentValue.RequireSupervisorToResolve
            && !currentUser.IsInRole(RoleNames.Supervisor)
            && !currentUser.IsInRole(RoleNames.Administrator))
        {
            throw new UnauthorizedAccessException("Resolving an alarm requires the Supervisor or Administrator role.");
        }

        var alarm = await db.Alarms.FirstOrDefaultAsync(a => a.Id == alarmId, cancellationToken)
            .ConfigureAwait(false);

        if (alarm is null || alarm.Status == AlarmStatus.Resolved)
        {
            return false;
        }

        var previous = alarm.Status;
        alarm.Status = AlarmStatus.Resolved;
        alarm.ResolvedByUserId = currentUser.UserId;
        alarm.ResolvedAt = clock.UtcNow;
        alarm.ResolutionNotes = notes;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.AlarmResolved,
            GateId = alarm.GateId,
            DocumentId = alarm.DocumentId,
            GateCycleId = alarm.GateCycleId,
            Epc = alarm.Epc,
            PreviousState = previous.ToString(),
            NewState = AlarmStatus.Resolved.ToString(),
            Details = notes
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Alarm>> GetActiveAsync(
        int? gateId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Alarms.Where(a => a.Status != AlarmStatus.Resolved);

        if (gateId is not null)
        {
            query = query.Where(a => a.GateId == gateId);
        }

        return await query
            .OrderByDescending(a => a.RaisedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Alarm> BuildAsync(RaiseAlarmRequest request, CancellationToken cancellationToken) => new()
    {
        AlarmId = await numbers.NextAlarmIdAsync(cancellationToken).ConfigureAwait(false),
        AlarmType = request.AlarmType,
        Status = AlarmStatus.Active,
        Message = request.Message,
        GateId = request.GateId,
        DocumentId = request.DocumentId,
        GateCycleId = request.GateCycleId,
        ReaderId = request.ReaderId,
        Epc = request.Epc,
        EpcList = request.Epcs.Count > 0 ? string.Join('\n', request.Epcs) : null,
        RaisedAt = clock.UtcNow
    };

    /// <summary>Best-effort side effects. Never allowed to fail the caller.</summary>
    private async Task AnnounceAsync(Alarm alarm, RaiseAlarmRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.AlarmRaisedAsync(new AlarmRaisedUpdate
            {
                AlarmId = alarm.AlarmId,
                AlarmType = alarm.AlarmType,
                GateCode = request.GateCode,
                DocumentNumber = request.DocumentNumber,
                CycleId = request.CycleId,
                Message = alarm.Message,
                Epc = alarm.Epc,
                Epcs = request.Epcs,
                Timestamp = alarm.RaisedAt
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alarm {AlarmId}", alarm.AlarmId);
        }

        if (options.CurrentValue.DriveGpioOutput && request.GateCode is { Length: > 0 } gate)
        {
            try
            {
                await indicator.SignalAlarmAsync(gate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to drive alarm output for gate {GateCode}", gate);
            }
        }
    }
}
