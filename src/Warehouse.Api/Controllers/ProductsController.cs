using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Documents;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Api.Controllers;

public sealed record ProductDto
{
    public required int Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Uom { get; init; }

    public int UnitsPerCarton { get; init; }

    public bool IsActive { get; init; }

    /// <summary>Tags registered against this product.</summary>
    public int TagCount { get; init; }

    /// <summary>Tags currently in stock.</summary>
    public int InStockCount { get; init; }
}

public sealed record UpsertProductRequest
{
    /// <summary>Style code, e.g. BR207. Unique.</summary>
    public required string Code { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Uom { get; init; }

    public int UnitsPerCarton { get; init; } = 1;
}

/// <summary>
/// Product (style) catalogue.
/// </summary>
/// <remarks>
/// A tag is registered against a product so stock can be aggregated by style
/// rather than only counted as loose tags. Products are created here or by the
/// import pipeline; none are hard-coded, and the seeder does not invent any.
/// </remarks>
[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController(
    IWarehouseDbContext db,
    IClock clock,
    IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.Products.AsNoTracking().AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p => p.Code.Contains(search) || p.Name.Contains(search));
        }

        var rows = await query
            .OrderBy(p => p.Code)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                Description = p.Description,
                Uom = p.Uom,
                UnitsPerCarton = p.UnitsPerCarton,
                IsActive = p.IsActive,
                TagCount = p.Tags.Count,
                InStockCount = p.Tags.Count(t => t.Status == EpcStatus.InStock)
            })
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                Description = p.Description,
                Uom = p.Uom,
                UnitsPerCarton = p.UnitsPerCarton,
                IsActive = p.IsActive,
                TagCount = p.Tags.Count,
                InStockCount = p.Tags.Count(t => t.Status == EpcStatus.InStock)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? NotFound() : Ok(product);
    }

    /// <summary>
    /// Creates a product, or returns the existing one when the code is already
    /// taken. Idempotent so a bulk load can be re-run without failing.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<IActionResult> Create(
        [FromBody] UpsertProductRequest request,
        CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new WarehouseValidationException("Product code is required.");
        }

        if (request.UnitsPerCarton < 1)
        {
            throw new WarehouseValidationException("Units per carton must be at least 1.");
        }

        var existing = await db.Products.FirstOrDefaultAsync(p => p.Code == code, cancellationToken);

        if (existing is not null)
        {
            return Ok(Map(existing));
        }

        var product = new Product
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(request.Name) ? code : request.Name.Trim(),
            Description = request.Description,
            Uom = request.Uom,
            UnitsPerCarton = request.UnitsPerCarton,
            IsActive = true,
            CreatedAt = clock.UtcNow
        };

        db.Products.Add(product);

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            Result = "PRODUCT_CREATED",
            Details = $"{product.Code} ({product.Name})"
        });

        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = product.Id }, Map(product));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpsertProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return NotFound();
        }

        if (request.UnitsPerCarton < 1)
        {
            throw new WarehouseValidationException("Units per carton must be at least 1.");
        }

        product.Name = string.IsNullOrWhiteSpace(request.Name) ? product.Name : request.Name.Trim();
        product.Description = request.Description ?? product.Description;
        product.Uom = request.Uom ?? product.Uom;
        product.UnitsPerCarton = request.UnitsPerCarton;
        product.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return Ok(Map(product));
    }

    private static ProductDto Map(Product p) => new()
    {
        Id = p.Id,
        Code = p.Code,
        Name = p.Name,
        Description = p.Description,
        Uom = p.Uom,
        UnitsPerCarton = p.UnitsPerCarton,
        IsActive = p.IsActive
    };
}
