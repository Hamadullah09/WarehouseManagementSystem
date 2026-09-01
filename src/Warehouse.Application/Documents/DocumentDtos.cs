using Warehouse.Domain;

namespace Warehouse.Application.Documents;

/// <summary>Request to create an INWARD or OUTWARD document.</summary>
public sealed record CreateDocumentRequest
{
    /// <summary>EPCs expected to cross the gate. Normalised and de-duplicated on receipt.</summary>
    public required IReadOnlyList<string> Epcs { get; init; }

    /// <summary>Operator the document is issued to. Defaults to the caller.</summary>
    public int? UserId { get; init; }

    /// <summary>Gate to release to. When set, the document is created already Released.</summary>
    public string? GateCode { get; init; }

    public string? Reference { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Per-EPC line on a document.</summary>
public sealed record DocumentItemDto
{
    public required string Epc { get; init; }

    public string? ItemCode { get; init; }

    public string? ItemName { get; init; }

    public string? CartonNumber { get; init; }

    public int Quantity { get; init; }

    public bool IsDetected { get; init; }

    public DateTimeOffset? DetectedAt { get; init; }
}

/// <summary>Document summary for list views.</summary>
public record DocumentSummaryDto
{
    public required int Id { get; init; }

    public required string DocumentNumber { get; init; }

    public required DocumentType Type { get; init; }

    public required DocumentStatus Status { get; init; }

    public string? UserDisplayName { get; init; }

    public string? GateCode { get; init; }

    public int ExpectedArticles { get; init; }

    public int DetectedArticles { get; init; }

    public int BalanceArticles { get; init; }

    public int ExpectedQuantity { get; init; }

    public int DetectedQuantity { get; init; }

    public int BalanceQuantity { get; init; }

    public int RetryCount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Full document, including expected/detected/balance EPC breakdown (§45).</summary>
public sealed record DocumentDetailDto : DocumentSummaryDto
{
    public string? Reference { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<DocumentItemDto> Items { get; init; } = [];

    /// <summary>EPCs confirmed by a passed cycle.</summary>
    public IReadOnlyList<string> DetectedEpcs { get; init; } = [];

    /// <summary>EPCs still outstanding.</summary>
    public IReadOnlyList<string> BalanceEpcs { get; init; } = [];

    public DateTimeOffset? ReleasedAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? CancelledReason { get; init; }
}

/// <summary>Paged list envelope.</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Total { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

/// <summary>Filters for the document list endpoint.</summary>
public sealed record DocumentQuery
{
    public DocumentType? Type { get; init; }

    public DocumentStatus? Status { get; init; }

    public string? GateCode { get; init; }

    public string? Search { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Raised when a request cannot be satisfied for business reasons. Surfaced as
/// HTTP 400/409 rather than 500, with the offending values attached.
/// </summary>
public sealed class WarehouseValidationException(string message, IReadOnlyList<string>? offending = null)
    : Exception(message)
{
    public IReadOnlyList<string> Offending { get; } = offending ?? [];
}
