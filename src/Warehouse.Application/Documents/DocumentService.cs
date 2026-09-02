using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Options;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Documents;

public interface IDocumentService
{
    Task<DocumentDetailDto> CreateAsync(
        DocumentType type,
        CreateDocumentRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentDetailDto?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto?> GetByNumberAsync(string documentNumber, CancellationToken cancellationToken = default);

    Task<PagedResult<DocumentSummaryDto>> ListAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> ReleaseAsync(int id, string gateCode, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> CancelAsync(int id, string? reason, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> RetryAsync(int id, CancellationToken cancellationToken = default);

    Task<DocumentDetailDto> UpdateAsync(
        int id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the document lifecycle: creation with a database-generated number,
/// release to a gate, cancellation and retry (§4, §5, §21).
/// </summary>
/// <remarks>
/// Creation is strict on purpose. A document whose EPCs are not all in the
/// catalogue would guarantee an unknown-EPC alarm at the gate, so it is
/// rejected up front with the offending values rather than allowed to fail
/// later in front of a driver.
/// </remarks>
public sealed class DocumentService(
    IWarehouseDbContext db,
    IClock clock,
    ICurrentUser currentUser,
    INumberGenerator numbers,
    IAuditService audit,
    IOptionsMonitor<DocumentOptions> options,
    ILogger<DocumentService> logger) : IDocumentService
{
    public async Task<DocumentDetailDto> CreateAsync(
        DocumentType type,
        CreateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cfg = options.CurrentValue;

        // Normalise first: casing and whitespace differences must never become
        // duplicate lines or phantom unknown tags.
        var epcs = request.Epcs
            .Select(Epc.Normalize)
            .Where(e => e.Length > 0)
            .Distinct(Epc.Comparer)
            .ToList();

        if (epcs.Count == 0)
        {
            throw new WarehouseValidationException("A document must contain at least one EPC.");
        }

        if (epcs.Count > cfg.MaxEpcsPerDocument)
        {
            throw new WarehouseValidationException(
                $"A document may contain at most {cfg.MaxEpcsPerDocument} EPCs; {epcs.Count} were supplied.");
        }

        var invalid = epcs.Where(e => !Epc.IsValid(e)).ToList();

        if (invalid.Count > 0)
        {
            throw new WarehouseValidationException("One or more EPCs are not valid hexadecimal values.", invalid);
        }

        var tags = await db.EpcTags
            .Where(t => epcs.Contains(t.Epc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = tags.Select(t => t.Epc).ToHashSet(Epc.Comparer);
        var missing = epcs.Where(e => !found.Contains(e)).ToList();

        if (missing.Count > 0)
        {
            throw new WarehouseValidationException(
                "One or more EPCs are not registered in the warehouse.", missing);
        }

        var unusable = tags
            .Where(t => !t.IsActive || t.Status is EpcStatus.Retired or EpcStatus.Blocked)
            .Select(t => t.Epc)
            .ToList();

        if (unusable.Count > 0)
        {
            throw new WarehouseValidationException(
                "One or more EPCs are retired, blocked or inactive.", unusable);
        }

        // Direction sanity: shipping a tag that is not in stock, or receiving
        // one that already is, means the plan disagrees with reality.
        var wrongState = type == DocumentType.Outward
            ? tags.Where(t => t.Status != EpcStatus.InStock).Select(t => t.Epc).ToList()
            : tags.Where(t => t.Status == EpcStatus.InStock).Select(t => t.Epc).ToList();

        if (wrongState.Count > 0)
        {
            var reason = type == DocumentType.Outward
                ? "are not currently in stock and cannot be shipped"
                : "are already in stock and cannot be received again";

            throw new WarehouseValidationException($"One or more EPCs {reason}.", wrongState);
        }

        Gate? gate = null;

        if (!string.IsNullOrWhiteSpace(request.GateCode))
        {
            gate = await FindGateAsync(request.GateCode, cancellationToken).ConfigureAwait(false);

            if (gate.Direction is { } allowed && allowed != type)
            {
                throw new WarehouseValidationException(
                    $"Gate {gate.Code} only handles {allowed} movements.");
            }

            // Creating straight onto a gate is a release, so it must obey the
            // same one-active-document rule. Without this, a second document
            // could silently displace the one the gate is already working, and
            // every read would be validated against the wrong expectation.
            await EnsureGateIsFreeAsync(gate, null, cancellationToken).ConfigureAwait(false);
        }

        var userId = request.UserId ?? currentUser.UserId;

        var userName = userId is null
            ? currentUser.UserName
            : await db.Users.Where(u => u.Id == userId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        var now = clock.UtcNow;
        var byEpc = tags.ToDictionary(t => t.Epc, Epc.Comparer);

        var document = new Document
        {
            DocumentNumber = await numbers.NextDocumentNumberAsync(type, cancellationToken).ConfigureAwait(false),
            Type = type,
            Status = gate is null ? DocumentStatus.Draft : DocumentStatus.Released,
            UserId = userId,
            UserDisplayName = userName,
            GateId = gate?.Id,
            Reference = request.Reference,
            Notes = request.Notes,
            ExpectedArticles = epcs.Count,
            ExpectedQuantity = epcs.Sum(e => byEpc[e].UnitQuantity),
            CreatedAt = now,
            ReleasedAt = gate is null ? null : now
        };

        foreach (var epc in epcs)
        {
            var tag = byEpc[epc];

            document.Items.Add(new DocumentItem
            {
                EpcTagId = tag.Id,
                Epc = epc,
                Quantity = tag.UnitQuantity
            });
        }

        db.Documents.Add(document);

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.DocumentCreated,
            DocumentNumber = document.DocumentNumber,
            GateId = gate?.Id,
            NewState = document.Status.ToString(),
            Result = $"{epcs.Count} articles, {document.ExpectedQuantity} units",
            Details = request.Reference
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Created {Type} document {DocumentNumber} with {Count} EPCs",
            type, document.DocumentNumber, epcs.Count);

        return (await GetAsync(document.Id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<DocumentDetailDto?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        return document is null ? null : MapDetail(document);
    }

    public async Task<DocumentDetailDto?> GetByNumberAsync(
        string documentNumber,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(d => d.DocumentNumber == documentNumber, cancellationToken)
            .ConfigureAwait(false);

        return document is null ? null : MapDetail(document);
    }

    public async Task<PagedResult<DocumentSummaryDto>> ListAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = db.Documents.AsNoTracking().Include(d => d.Gate).AsQueryable();

        if (query.Type is { } type)
        {
            q = q.Where(d => d.Type == type);
        }

        if (query.Status is { } status)
        {
            q = q.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.GateCode))
        {
            q = q.Where(d => d.Gate != null && d.Gate.Code == query.GateCode);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d => d.DocumentNumber.Contains(term)
                          || (d.Reference != null && d.Reference.Contains(term)));
        }

        if (query.From is { } from)
        {
            q = q.Where(d => d.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(d => d.CreatedAt <= to);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 500);

        var items = await q
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<DocumentSummaryDto>
        {
            Items = items.Select(MapSummary).ToList(),
            Total = total,
            Page = page,
            PageSize = size
        };
    }

    public async Task<DocumentDetailDto> ReleaseAsync(
        int id,
        string gateCode,
        CancellationToken cancellationToken = default)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Document {id} was not found.");

        if (document.Status is not (DocumentStatus.Draft or DocumentStatus.Released))
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} is {document.Status} and cannot be released.");
        }

        var gate = await FindGateAsync(gateCode, cancellationToken).ConfigureAwait(false);

        if (gate.Direction is { } allowed && allowed != document.Type)
        {
            throw new WarehouseValidationException($"Gate {gate.Code} only handles {allowed} movements.");
        }

        await EnsureGateIsFreeAsync(gate, document.Id, cancellationToken).ConfigureAwait(false);

        var previous = document.Status;
        document.GateId = gate.Id;
        document.Status = DocumentStatus.Released;
        document.ReleasedAt = clock.UtcNow;

        gate.ActiveDocumentId = document.Id;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.DocumentReleased,
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            GateId = gate.Id,
            PreviousState = previous.ToString(),
            NewState = document.Status.ToString()
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await GetAsync(document.Id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<DocumentDetailDto> CancelAsync(
        int id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Document {id} was not found.");

        if (document.Status is DocumentStatus.Completed or DocumentStatus.Cancelled)
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} is already {document.Status}.");
        }

        var previous = document.Status;
        document.Status = DocumentStatus.Cancelled;
        document.CancelledAt = clock.UtcNow;
        document.CancelledReason = reason;

        if (document.GateId is { } gateId)
        {
            var gate = await db.Gates.FirstOrDefaultAsync(g => g.Id == gateId, cancellationToken)
                .ConfigureAwait(false);

            if (gate?.ActiveDocumentId == document.Id)
            {
                gate.ActiveDocumentId = null;
            }
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.DocumentCancelled,
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            GateId = document.GateId,
            PreviousState = previous.ToString(),
            NewState = document.Status.ToString(),
            Details = reason
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await GetAsync(document.Id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<DocumentDetailDto> RetryAsync(int id, CancellationToken cancellationToken = default)
    {
        var document = await db.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Document {id} was not found.");

        if (document.Status is DocumentStatus.Completed or DocumentStatus.Cancelled)
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} is {document.Status} and cannot be retried.");
        }

        var cfg = options.CurrentValue;

        if (document.RetryCount >= cfg.MaxRetries)
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} has reached the retry limit of {cfg.MaxRetries}. "
                + "A supervisor must investigate.");
        }

        var previous = document.Status;

        // A retry re-runs the whole movement, so detection state is cleared.
        // Committed inventory is not touched: an EPC confirmed by a passed
        // cycle stays moved, and the retry only re-attempts the remainder.
        foreach (var item in document.Items.Where(i => !i.IsDetected))
        {
            item.DetectedAt = null;
            item.DetectedByCycleId = null;
        }

        document.Status = document.GateId is null ? DocumentStatus.Draft : DocumentStatus.Released;
        document.RetryCount++;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.DocumentRetried,
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            GateId = document.GateId,
            PreviousState = previous.ToString(),
            NewState = document.Status.ToString(),
            Result = $"retry {document.RetryCount} of {cfg.MaxRetries}"
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await GetAsync(document.Id, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Rejects binding a document to a gate that is already working one.
    /// </summary>
    /// <remarks>
    /// Two live plans on one physical gate would make every read ambiguous:
    /// an EPC expected by the other document would look unexpected here.
    /// </remarks>
    private async Task EnsureGateIsFreeAsync(
        Gate gate,
        int? excludingDocumentId,
        CancellationToken cancellationToken)
    {
        var conflicting = await db.Documents
            .Where(d => d.GateId == gate.Id
                     && (d.Status == DocumentStatus.Released || d.Status == DocumentStatus.InProgress))
            .Where(d => excludingDocumentId == null || d.Id != excludingDocumentId)
            .Select(d => d.DocumentNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conflicting is not null)
        {
            throw new WarehouseValidationException(
                $"Gate {gate.Code} already has an active document ({conflicting}). "
                + "Complete or cancel it first.");
        }
    }

    public async Task<DocumentDetailDto> UpdateAsync(
        int id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var document = await db.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Document {id} was not found.");

        if (document.Status is DocumentStatus.Completed or DocumentStatus.Cancelled)
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} is {document.Status} and cannot be edited.");
        }

        document.Reference = request.Reference ?? document.Reference;
        document.Notes = request.Notes ?? document.Notes;

        if (request.Epcs is { } replacement)
        {
            if (document.Status != DocumentStatus.Draft)
            {
                throw new WarehouseValidationException(
                    $"Document {document.DocumentNumber} has been released. Its EPC list can no longer be changed; "
                    + "cancel it and raise a new one instead.");
            }

            var resolved = await ResolveEpcsAsync(document.Type, replacement, cancellationToken)
                .ConfigureAwait(false);

            db.DocumentItems.RemoveRange(document.Items);
            document.Items.Clear();

            foreach (var tag in resolved)
            {
                document.Items.Add(new DocumentItem
                {
                    EpcTagId = tag.Id,
                    Epc = tag.Epc,
                    Quantity = tag.UnitQuantity
                });
            }

            document.ExpectedArticles = resolved.Count;
            document.ExpectedQuantity = resolved.Sum(t => t.UnitQuantity);
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            Result = "DOCUMENT_UPDATED",
            Details = request.Epcs is null ? "details" : $"{document.ExpectedArticles} EPCs"
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await GetAsync(document.Id, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Removes a document outright.
    /// </summary>
    /// <remarks>
    /// Only a draft or a cancelled document may go. Anything that has been
    /// through a gate is referenced by cycles, alarms and the movement ledger,
    /// and deleting it would leave an audit trail pointing at nothing. Cancel
    /// those instead -- that is what cancellation is for.
    /// </remarks>
    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var document = await db.Documents
            .Include(d => d.Items)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Document {id} was not found.");

        if (document.Status is not (DocumentStatus.Draft or DocumentStatus.Cancelled))
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} is {document.Status} and cannot be deleted. "
                + "Cancel it instead, so its history stays intact.");
        }

        var hasCycles = await db.GateCycles.AnyAsync(c => c.DocumentId == id, cancellationToken)
            .ConfigureAwait(false);

        if (hasCycles)
        {
            throw new WarehouseValidationException(
                $"Document {document.DocumentNumber} has gate cycles recorded against it and cannot be deleted.");
        }

        if (document.GateId is { } gateId)
        {
            var gate = await db.Gates.FirstOrDefaultAsync(g => g.Id == gateId, cancellationToken)
                .ConfigureAwait(false);

            if (gate?.ActiveDocumentId == document.Id)
            {
                gate.ActiveDocumentId = null;
            }
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.DocumentCancelled,
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            PreviousState = document.Status.ToString(),
            Result = "DOCUMENT_DELETED"
        });

        db.DocumentItems.RemoveRange(document.Items);
        db.Documents.Remove(document);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Deleted document {DocumentNumber}", document.DocumentNumber);
    }

    /// <summary>Validates an EPC list for a document of the given type.</summary>
    private async Task<List<EpcTag>> ResolveEpcsAsync(
        DocumentType type,
        IReadOnlyList<string> epcs,
        CancellationToken cancellationToken)
    {
        var cfg = options.CurrentValue;

        var normalised = epcs
            .Select(Epc.Normalize)
            .Where(e => e.Length > 0)
            .Distinct(Epc.Comparer)
            .ToList();

        if (normalised.Count == 0)
        {
            throw new WarehouseValidationException("A document must contain at least one EPC.");
        }

        if (normalised.Count > cfg.MaxEpcsPerDocument)
        {
            throw new WarehouseValidationException(
                $"A document may contain at most {cfg.MaxEpcsPerDocument} EPCs; {normalised.Count} were supplied.");
        }

        var tags = await db.EpcTags
            .Where(t => normalised.Contains(t.Epc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = tags.Select(t => t.Epc).ToHashSet(Epc.Comparer);
        var missing = normalised.Where(e => !found.Contains(e)).ToList();

        if (missing.Count > 0)
        {
            throw new WarehouseValidationException(
                "One or more EPCs are not registered in the warehouse.", missing);
        }

        var wrongState = type == DocumentType.Outward
            ? tags.Where(t => t.Status != EpcStatus.InStock).Select(t => t.Epc).ToList()
            : tags.Where(t => t.Status == EpcStatus.InStock).Select(t => t.Epc).ToList();

        if (wrongState.Count > 0)
        {
            var reason = type == DocumentType.Outward
                ? "are not currently in stock and cannot be shipped"
                : "are already in stock and cannot be received again";

            throw new WarehouseValidationException($"One or more EPCs {reason}.", wrongState);
        }

        return tags;
    }

    private async Task<Gate> FindGateAsync(string gateCode, CancellationToken cancellationToken)
    {
        var gate = await db.Gates.FirstOrDefaultAsync(g => g.Code == gateCode, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WarehouseValidationException($"Gate {gateCode} was not found.");

        if (!gate.IsActive)
        {
            throw new WarehouseValidationException($"Gate {gate.Code} is not active.");
        }

        return gate;
    }

    private Task<Document?> LoadAsync(
        System.Linq.Expressions.Expression<Func<Document, bool>> predicate,
        CancellationToken cancellationToken) =>
        db.Documents
            .AsNoTracking()
            .Include(d => d.Gate)
            .Include(d => d.Items)
            .ThenInclude(i => i.EpcTag)
            .ThenInclude(t => t.Product)
            .FirstOrDefaultAsync(predicate, cancellationToken);

    private static DocumentSummaryDto MapSummary(Document d) => new()
    {
        Id = d.Id,
        DocumentNumber = d.DocumentNumber,
        Type = d.Type,
        Status = d.Status,
        UserDisplayName = d.UserDisplayName,
        GateCode = d.Gate?.Code,
        Reference = d.Reference,
        ExpectedArticles = d.ExpectedArticles,
        DetectedArticles = d.DetectedArticles,
        BalanceArticles = d.BalanceArticles,
        ExpectedQuantity = d.ExpectedQuantity,
        DetectedQuantity = d.DetectedQuantity,
        BalanceQuantity = d.BalanceQuantity,
        RetryCount = d.RetryCount,
        CreatedAt = d.CreatedAt,
        CompletedAt = d.CompletedAt
    };

    private static DocumentDetailDto MapDetail(Document d)
    {
        var items = d.Items
            .OrderBy(i => i.Epc, Epc.Comparer)
            .Select(i => new DocumentItemDto
            {
                Epc = i.Epc,
                ItemCode = i.EpcTag?.ItemCode,
                ItemName = i.EpcTag?.ItemName,
                CartonNumber = i.EpcTag?.CartonNumber,
                ProductCode = i.EpcTag?.Product?.Code,
                Quantity = i.Quantity,
                IsDetected = i.IsDetected,
                DetectedAt = i.DetectedAt
            })
            .ToList();

        return new DocumentDetailDto
        {
            Id = d.Id,
            DocumentNumber = d.DocumentNumber,
            Type = d.Type,
            Status = d.Status,
            UserDisplayName = d.UserDisplayName,
            GateCode = d.Gate?.Code,
            Reference = d.Reference,
            ExpectedArticles = d.ExpectedArticles,
            DetectedArticles = d.DetectedArticles,
            BalanceArticles = d.BalanceArticles,
            ExpectedQuantity = d.ExpectedQuantity,
            DetectedQuantity = d.DetectedQuantity,
            BalanceQuantity = d.BalanceQuantity,
            RetryCount = d.RetryCount,
            CreatedAt = d.CreatedAt,
            CompletedAt = d.CompletedAt,
            ReleasedAt = d.ReleasedAt,
            CancelledAt = d.CancelledAt,
            CancelledReason = d.CancelledReason,
            Notes = d.Notes,
            Items = items,
            DetectedEpcs = items.Where(i => i.IsDetected).Select(i => i.Epc).ToList(),
            BalanceEpcs = items.Where(i => !i.IsDetected).Select(i => i.Epc).ToList()
        };
    }
}
