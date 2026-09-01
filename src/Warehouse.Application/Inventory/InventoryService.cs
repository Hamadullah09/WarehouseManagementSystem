using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Inventory;

public sealed record InventoryCommitRequest
{
    public required long GateCycleId { get; init; }

    public required string CycleId { get; init; }

    public required int DocumentId { get; init; }

    /// <summary>Expected EPCs confirmed by this cycle. Only these move stock.</summary>
    public required IReadOnlyList<string> MatchedEpcs { get; init; }

    public int? GateId { get; init; }
}

public sealed record InventoryCommitResult
{
    public required bool Committed { get; init; }

    public int MovedArticles { get; init; }

    public int MovedQuantity { get; init; }

    public bool DocumentCompleted { get; init; }

    /// <summary>Populated when <see cref="Committed"/> is false.</summary>
    public string? Reason { get; init; }
}

public interface IInventoryService
{
    /// <summary>
    /// Applies one validated gate cycle to stock, atomically. Safe to call
    /// twice: the second call is a no-op.
    /// </summary>
    Task<InventoryCommitResult> CommitCycleAsync(
        InventoryCommitRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only component permitted to move stock (§27).
/// </summary>
/// <remarks>
/// Nothing here runs until the validation engine has already returned a pass.
/// Everything the movement touches -- tag status, document lines, document
/// counters, aggregate stock, the movement ledger, the cycle's committed flag
/// and the audit rows -- commits in a single database transaction, so a
/// failure part-way leaves no half-moved load.
///
/// Idempotency is enforced twice: by the cycle's <c>InventoryCommitted</c>
/// flag, and by a per-tag check that the same cycle is not re-applied. A
/// replayed gate event therefore cannot double-count stock (§28).
/// </remarks>
public sealed class InventoryService(
    IWarehouseDbContext db,
    IClock clock,
    IAuditService audit,
    ILogger<InventoryService> logger) : IInventoryService
{
    public async Task<InventoryCommitResult> CommitCycleAsync(
        InventoryCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // The whole commit is one retriable transactional unit: on a
            // transient SQL failure it is retried from the top rather than
            // leaving stock half-moved.
            return await db.ExecuteInTransactionAsync(async token =>
            {
            var cycle = await db.GateCycles
                .FirstOrDefaultAsync(c => c.Id == request.GateCycleId, token)
                .ConfigureAwait(false);

            if (cycle is null)
            {
                return Rejected($"Gate cycle {request.CycleId} was not found.");
            }

            if (cycle.InventoryCommitted)
            {
                // Replay. Not an error: the physical movement already happened
                // and was already accounted for.
                logger.LogInformation(
                    "Gate cycle {CycleId} already committed; ignoring duplicate commit", request.CycleId);

                return Rejected("Cycle inventory was already committed.");
            }

            var document = await db.Documents
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == request.DocumentId, token)
                .ConfigureAwait(false);

            if (document is null)
            {
                return Rejected($"Document {request.DocumentId} was not found.");
            }

            if (document.Status is DocumentStatus.Cancelled or DocumentStatus.Completed)
            {
                return Rejected($"Document {document.DocumentNumber} is {document.Status}.");
            }

            var matched = request.MatchedEpcs.Select(Epc.Normalize).Distinct(Epc.Comparer).ToList();

            if (matched.Count == 0)
            {
                return Rejected("No matched EPCs to commit.");
            }

            var tags = await db.EpcTags
                .Where(t => matched.Contains(t.Epc))
                .ToListAsync(token)
                .ConfigureAwait(false);

            var now = clock.UtcNow;
            var newStatus = document.Type == DocumentType.Inward ? EpcStatus.InStock : EpcStatus.Shipped;
            var sign = document.Type == DocumentType.Inward ? 1 : -1;

            var itemsByEpc = document.Items.ToDictionary(i => i.Epc, Epc.Comparer);
            var quantityByProduct = new Dictionary<int, (int Articles, int Quantity)>();

            var movedArticles = 0;
            var movedQuantity = 0;

            foreach (var tag in tags)
            {
                // Per-tag replay guard, independent of the cycle flag.
                if (tag.LastGateCycleId == cycle.Id)
                {
                    continue;
                }

                if (!itemsByEpc.TryGetValue(tag.Epc, out var item) || item.IsDetected)
                {
                    continue;
                }

                var previousStatus = tag.Status;

                tag.Status = newStatus;
                tag.LastGateCycleId = cycle.Id;
                tag.LastMovementAt = now;
                tag.UpdatedAt = now;

                item.IsDetected = true;
                item.DetectedAt = now;
                item.DetectedByCycleId = cycle.Id;

                db.InventoryMovements.Add(new InventoryMovement
                {
                    EpcTagId = tag.Id,
                    GateCycleId = cycle.Id,
                    DocumentId = document.Id,
                    Direction = document.Type,
                    Quantity = tag.UnitQuantity,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    OccurredAt = now
                });

                movedArticles++;
                movedQuantity += tag.UnitQuantity;

                if (tag.ProductId is { } productId)
                {
                    var current = quantityByProduct.GetValueOrDefault(productId);
                    quantityByProduct[productId] = (current.Articles + 1, current.Quantity + tag.UnitQuantity);
                }
            }

            if (movedArticles == 0)
            {
                return Rejected("Every matched EPC had already been committed.");
            }

            await ApplyAggregatesAsync(quantityByProduct, sign, now, token).ConfigureAwait(false);

            document.DetectedArticles += movedArticles;
            document.DetectedQuantity += movedQuantity;

            var completed = document.Items.All(i => i.IsDetected);

            if (completed)
            {
                document.Status = DocumentStatus.Completed;
                document.CompletedAt = now;

                if (document.GateId is { } gateId)
                {
                    var gate = await db.Gates.FirstOrDefaultAsync(g => g.Id == gateId, token)
                        .ConfigureAwait(false);

                    if (gate?.ActiveDocumentId == document.Id)
                    {
                        gate.ActiveDocumentId = null;
                    }
                }
            }
            else
            {
                document.Status = DocumentStatus.InProgress;
            }

            cycle.InventoryCommitted = true;

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.InventoryCommitted,
                GateId = request.GateId,
                DocumentId = document.Id,
                DocumentNumber = document.DocumentNumber,
                GateCycleId = cycle.Id,
                CycleId = request.CycleId,
                Result = $"{movedArticles} articles, {movedQuantity} units {document.Type}",
                NewState = document.Status.ToString()
            });

            if (completed)
            {
                audit.Enlist(new AuditEntry
                {
                    Action = AuditAction.DocumentCompleted,
                    DocumentId = document.Id,
                    DocumentNumber = document.DocumentNumber,
                    GateId = request.GateId,
                    GateCycleId = cycle.Id,
                    CycleId = request.CycleId,
                    NewState = DocumentStatus.Completed.ToString()
                });
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            logger.LogInformation(
                "Committed cycle {CycleId}: {Articles} articles / {Quantity} units on {DocumentNumber} (completed={Completed})",
                request.CycleId, movedArticles, movedQuantity, document.DocumentNumber, completed);

            return new InventoryCommitResult
            {
                Committed = true,
                MovedArticles = movedArticles,
                MovedQuantity = movedQuantity,
                DocumentCompleted = completed
            };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The transaction has already rolled back; record why.
            logger.LogError(ex, "Inventory commit failed for cycle {CycleId}; rolled back", request.CycleId);

            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditAction.InventoryRolledBack,
                DocumentId = request.DocumentId,
                GateCycleId = request.GateCycleId,
                CycleId = request.CycleId,
                GateId = request.GateId,
                Result = "ROLLED_BACK",
                Details = ex.Message
            }, CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    private async Task ApplyAggregatesAsync(
        Dictionary<int, (int Articles, int Quantity)> byProduct,
        int sign,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (byProduct.Count == 0)
        {
            return;
        }

        var productIds = byProduct.Keys.ToList();

        var rows = await db.Inventory
            .Where(i => productIds.Contains(i.ProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = rows.ToDictionary(r => r.ProductId);

        foreach (var (productId, delta) in byProduct)
        {
            if (!byId.TryGetValue(productId, out var row))
            {
                row = new InventoryItem { ProductId = productId };
                db.Inventory.Add(row);
            }

            // Clamped at zero: an outward movement can never drive aggregate
            // stock negative, which would otherwise mask a data problem.
            row.OnHandArticles = Math.Max(0, row.OnHandArticles + (sign * delta.Articles));
            row.OnHandQuantity = Math.Max(0, row.OnHandQuantity + (sign * delta.Quantity));
            row.UpdatedAt = now;
        }
    }

    private static InventoryCommitResult Rejected(string reason) =>
        new() { Committed = false, Reason = reason };
}
