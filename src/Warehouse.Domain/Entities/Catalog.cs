namespace Warehouse.Domain.Entities;

public class Product
{
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Uom { get; set; }

    /// <summary>Units contained in one carton of this product. Drives quantity roll-ups.</summary>
    public int UnitsPerCarton { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<EpcTag> Tags { get; set; } = [];
}

/// <summary>
/// One physical RFID tag and the business record it carries (§3).
/// </summary>
/// <remarks>
/// <see cref="Epc"/> is unique and indexed; it is the join key for every
/// gate read. Stored uppercase and whitespace-free so reader-supplied casing
/// can never cause a false "unknown EPC".
/// </remarks>
public class EpcTag
{
    public long Id { get; set; }

    public string Epc { get; set; } = string.Empty;

    public string? ItemCode { get; set; }

    public string? ItemName { get; set; }

    public string? SerialNumber { get; set; }

    public string? CartonNumber { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public string? Description { get; set; }

    /// <summary>Units of stock this tag represents. Usually the carton quantity.</summary>
    public int UnitQuantity { get; set; } = 1;

    public EpcStatus Status { get; set; } = EpcStatus.Registered;

    public bool IsActive { get; set; } = true;

    /// <summary>Last gate cycle that moved this tag. Used to reject replayed movements.</summary>
    public long? LastGateCycleId { get; set; }

    public DateTimeOffset? LastMovementAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token; guards concurrent gate cycles touching one tag.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>Aggregate stock position per product, maintained transactionally with tag movement.</summary>
public class InventoryItem
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Number of tagged cartons currently in stock.</summary>
    public int OnHandArticles { get; set; }

    /// <summary>Sum of unit quantities currently in stock.</summary>
    public int OnHandQuantity { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public byte[]? RowVersion { get; set; }
}

/// <summary>Append-only ledger of every committed stock movement.</summary>
public class InventoryMovement
{
    public long Id { get; set; }

    public long EpcTagId { get; set; }
    public EpcTag EpcTag { get; set; } = null!;

    public long GateCycleId { get; set; }

    public int DocumentId { get; set; }

    public DocumentType Direction { get; set; }

    public int Quantity { get; set; }

    public EpcStatus PreviousStatus { get; set; }

    public EpcStatus NewStatus { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
