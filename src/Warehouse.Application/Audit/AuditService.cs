using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Audit;

/// <summary>One auditable fact. Everything optional except the action itself.</summary>
public sealed record AuditEntry
{
    public required AuditAction Action { get; init; }

    public int? UserId { get; init; }

    public string? UserName { get; init; }

    public int? GateId { get; init; }

    public int? DocumentId { get; init; }

    public string? DocumentNumber { get; init; }

    public long? GateCycleId { get; init; }

    public string? CycleId { get; init; }

    public int? ReaderId { get; init; }

    public string? Epc { get; init; }

    public string? PreviousState { get; init; }

    public string? NewState { get; init; }

    public string? Result { get; init; }

    public string? Details { get; init; }

    public string? CorrelationId { get; init; }
}

public interface IAuditService
{
    /// <summary>
    /// Queues an audit row on the current change tracker without saving.
    /// Use inside an open transaction so the trail commits or rolls back with
    /// the business change it describes (§27, §32).
    /// </summary>
    void Enlist(AuditEntry entry);

    /// <summary>Writes and saves immediately. For events outside a transaction.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Writes a batch and saves once.</summary>
    Task WriteManyAsync(IEnumerable<AuditEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only audit trail (§32).
/// </summary>
/// <remarks>
/// The caller's identity is captured at write time rather than referenced, so
/// the trail stays readable after a user is renamed or deactivated. Audit
/// writes never throw into the caller: losing an audit row must not roll back
/// a movement that physically happened, so failures are logged and swallowed
/// in the fire-and-forget path only. Enlisted rows share the caller's
/// transaction and therefore do participate in a rollback, which is what we
/// want -- a movement that did not commit should leave no trace claiming it did.
/// </remarks>
public sealed class AuditService(
    IWarehouseDbContext db,
    IClock clock,
    ICurrentUser currentUser,
    ILogger<AuditService> logger) : IAuditService
{
    public void Enlist(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.AuditLogs.Add(Map(entry));
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            db.AuditLogs.Add(Map(entry));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit entry {Action}", entry.Action);
        }
    }

    public async Task WriteManyAsync(IEnumerable<AuditEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        try
        {
            foreach (var entry in entries)
            {
                db.AuditLogs.Add(Map(entry));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit batch");
        }
    }

    private AuditLog Map(AuditEntry e) => new()
    {
        Action = e.Action,
        UserId = e.UserId ?? currentUser.UserId,
        UserName = e.UserName ?? currentUser.UserName,
        GateId = e.GateId,
        DocumentId = e.DocumentId,
        DocumentNumber = e.DocumentNumber,
        GateCycleId = e.GateCycleId,
        CycleId = e.CycleId,
        ReaderId = e.ReaderId,
        Epc = e.Epc,
        PreviousState = e.PreviousState,
        NewState = e.NewState,
        Result = e.Result,
        Details = e.Details,
        CorrelationId = e.CorrelationId,
        IpAddress = currentUser.IpAddress,
        OccurredAt = clock.UtcNow
    };
}
