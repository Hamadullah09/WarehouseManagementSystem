using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Epcs;

/// <summary>One row of an EPC import file.</summary>
public sealed class EpcImportRow
{
    public string? Epc { get; set; }

    public string? ItemCode { get; set; }

    public string? ItemName { get; set; }

    public string? SerialNumber { get; set; }

    public string? CartonNumber { get; set; }

    public string? ProductCode { get; set; }

    public string? Description { get; set; }

    public int? UnitQuantity { get; set; }

    public string? Status { get; set; }
}

public sealed record EpcImportError(int Row, string? Epc, string Reason);

public sealed record EpcImportResult
{
    public int TotalRows { get; init; }

    public int Imported { get; init; }

    public int Updated { get; init; }

    public int Skipped { get; init; }

    public IReadOnlyList<EpcImportError> Errors { get; init; } = [];

    public bool Success => Errors.Count == 0;
}

public interface IEpcImportService
{
    /// <summary>Imports EPCs from a CSV stream.</summary>
    Task<EpcImportResult> ImportCsvAsync(
        Stream csv,
        bool updateExisting,
        CancellationToken cancellationToken = default);

    /// <summary>Imports already-parsed rows. Used by tests and by other ingestion paths.</summary>
    Task<EpcImportResult> ImportAsync(
        IReadOnlyList<EpcImportRow> rows,
        bool updateExisting,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bulk EPC registration with duplicate and validity checking (§44).
/// </summary>
/// <remarks>
/// The catalogue is the thing every gate decision is measured against, so the
/// import is deliberately unforgiving: a malformed EPC, a duplicate inside the
/// file, or a reference to a product that does not exist is reported as a row
/// error rather than quietly dropped. Rows that pass are applied in one
/// transaction; nothing is half-imported.
/// </remarks>
public sealed class EpcImportService(
    IWarehouseDbContext db,
    IClock clock,
    IAuditService audit,
    ILogger<EpcImportService> logger) : IEpcImportService
{
    public async Task<EpcImportResult> ImportCsvAsync(
        Stream csv,
        bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
            PrepareHeaderForMatch = args => args.Header.Replace(" ", string.Empty).ToUpperInvariant()
        };

        List<EpcImportRow> rows;

        using (var reader = new StreamReader(csv, leaveOpen: true))
        using (var parser = new CsvReader(reader, config))
        {
            rows = parser.GetRecords<EpcImportRow>().ToList();
        }

        return await ImportAsync(rows, updateExisting, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EpcImportResult> ImportAsync(
        IReadOnlyList<EpcImportRow> rows,
        bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var errors = new List<EpcImportError>();
        var seen = new HashSet<string>(Epc.Comparer);
        var valid = new List<(int Row, string Epc, EpcImportRow Data)>();

        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2; // header is line 1
            var row = rows[i];
            var epc = Epc.Normalize(row.Epc);

            if (epc.Length == 0)
            {
                errors.Add(new EpcImportError(line, row.Epc, "EPC is empty."));
                continue;
            }

            if (!Epc.IsValid(epc))
            {
                errors.Add(new EpcImportError(line, epc,
                    "EPC must be an even-length hexadecimal string of at most 128 characters."));

                continue;
            }

            if (!seen.Add(epc))
            {
                errors.Add(new EpcImportError(line, epc, "Duplicate EPC within the import file."));
                continue;
            }

            if (row.UnitQuantity is < 1)
            {
                errors.Add(new EpcImportError(line, epc, "Unit quantity must be at least 1."));
                continue;
            }

            if (row.Status is { Length: > 0 } status && !Enum.TryParse<EpcStatus>(status, true, out _))
            {
                errors.Add(new EpcImportError(line, epc,
                    $"Unrecognised status '{status}'. Expected one of: {string.Join(", ", Enum.GetNames<EpcStatus>())}."));

                continue;
            }

            valid.Add((line, epc, row));
        }

        var epcs = valid.Select(v => v.Epc).ToList();

        var productCodes = valid
            .Select(v => v.Data.ProductCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var now = clock.UtcNow;
        var imported = 0;
        var updated = 0;
        var skipped = 0;

        try
        {
            // Loads live inside the transactional unit: on a transient retry
            // the change tracker is cleared, so entities fetched outside would
            // be detached and their edits silently lost.
            await db.ExecuteInTransactionAsync(async token =>
            {
                imported = 0;
                updated = 0;
                skipped = 0;
                errors.RemoveAll(e => e.Reason.StartsWith("Product '", StringComparison.Ordinal));

                var products = productCodes.Count == 0
                    ? []
                    : await db.Products
                        .Where(p => productCodes.Contains(p.Code))
                        .ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase, token)
                        .ConfigureAwait(false);

                var existing = await db.EpcTags
                    .Where(t => epcs.Contains(t.Epc))
                    .ToDictionaryAsync(t => t.Epc, Epc.Comparer, token)
                    .ConfigureAwait(false);

                foreach (var (line, epc, data) in valid)
                {
                    int? productId = null;

                    if (!string.IsNullOrWhiteSpace(data.ProductCode))
                    {
                        if (!products.TryGetValue(data.ProductCode.Trim(), out var product))
                        {
                            errors.Add(new EpcImportError(line, epc,
                                $"Product '{data.ProductCode}' does not exist."));

                            continue;
                        }

                        productId = product.Id;
                    }

                    var status = data.Status is { Length: > 0 } s
                        ? Enum.Parse<EpcStatus>(s, true)
                        : (EpcStatus?)null;

                    if (existing.TryGetValue(epc, out var tag))
                    {
                        if (!updateExisting)
                        {
                            skipped++;
                            continue;
                        }

                        tag.ItemCode = data.ItemCode ?? tag.ItemCode;
                        tag.ItemName = data.ItemName ?? tag.ItemName;
                        tag.SerialNumber = data.SerialNumber ?? tag.SerialNumber;
                        tag.CartonNumber = data.CartonNumber ?? tag.CartonNumber;
                        tag.Description = data.Description ?? tag.Description;
                        tag.ProductId = productId ?? tag.ProductId;
                        tag.UnitQuantity = data.UnitQuantity ?? tag.UnitQuantity;
                        tag.Status = status ?? tag.Status;
                        tag.UpdatedAt = now;

                        updated++;
                    }
                    else
                    {
                        db.EpcTags.Add(new EpcTag
                        {
                            Epc = epc,
                            ItemCode = data.ItemCode,
                            ItemName = data.ItemName,
                            SerialNumber = data.SerialNumber,
                            CartonNumber = data.CartonNumber,
                            Description = data.Description,
                            ProductId = productId,
                            UnitQuantity = data.UnitQuantity ?? 1,
                            Status = status ?? EpcStatus.Registered,
                            IsActive = true,
                            CreatedAt = now
                        });

                        imported++;
                    }
                }

                audit.Enlist(new AuditEntry
                {
                    Action = AuditAction.EpcImported,
                    Result = $"{imported} added, {updated} updated, {skipped} skipped, {errors.Count} rejected"
                });

                return await db.SaveChangesAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EPC import failed; rolled back");
            throw;
        }

        logger.LogInformation(
            "EPC import: {Imported} added, {Updated} updated, {Skipped} skipped, {Errors} rejected",
            imported, updated, skipped, errors.Count);

        return new EpcImportResult
        {
            TotalRows = rows.Count,
            Imported = imported,
            Updated = updated,
            Skipped = skipped,
            Errors = errors
        };
    }
}
