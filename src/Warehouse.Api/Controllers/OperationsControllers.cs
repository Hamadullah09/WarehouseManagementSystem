using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Alarms;
using Warehouse.Application.Documents;
using Warehouse.Application.Epcs;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Api.Controllers;

// ------------------------------------------------------------------- alarms

public sealed record AlarmDto
{
    public required long Id { get; init; }

    public required string AlarmId { get; init; }

    public required AlarmType AlarmType { get; init; }

    public required AlarmStatus Status { get; init; }

    public required string Message { get; init; }

    public string? GateCode { get; init; }

    public string? DocumentNumber { get; init; }

    public string? CycleId { get; init; }

    public string? Epc { get; init; }

    public IReadOnlyList<string> Epcs { get; init; } = [];

    public DateTimeOffset RaisedAt { get; init; }

    public string? ResolvedBy { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolutionNotes { get; init; }
}

public sealed record ResolveAlarmBody
{
    [MaxLength(2048)]
    public string? Notes { get; init; }
}

[ApiController]
[Route("api/alarms")]
[Authorize]
public sealed class AlarmsController(IAlarmService alarms, IWarehouseDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AlarmDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AlarmDto>>> List(
        [FromQuery] AlarmStatus? status,
        [FromQuery] string? gateCode,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.Alarms
            .AsNoTracking()
            .Include(a => a.Gate)
            .Include(a => a.Document)
            .Include(a => a.GateCycle)
            .Include(a => a.ResolvedByUser)
            .AsQueryable();

        if (status is { } s)
        {
            query = query.Where(a => a.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(gateCode))
        {
            query = query.Where(a => a.Gate != null && a.Gate.Code == gateCode);
        }

        var rows = await query
            .OrderByDescending(a => a.RaisedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(Map).ToList());
    }

    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<AlarmDto>>> Active(CancellationToken cancellationToken)
    {
        var rows = await db.Alarms
            .AsNoTracking()
            .Include(a => a.Gate)
            .Include(a => a.Document)
            .Include(a => a.GateCycle)
            .Include(a => a.ResolvedByUser)
            .Where(a => a.Status != AlarmStatus.Resolved)
            .OrderByDescending(a => a.RaisedAt)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(Map).ToList());
    }

    [HttpPost("{id:long}/acknowledge")]
    public async Task<IActionResult> Acknowledge(long id, CancellationToken cancellationToken) =>
        await alarms.AcknowledgeAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Resolves an alarm. Requires Supervisor or Administrator by default (§18).</summary>
    [HttpPost("{id:long}/resolve")]
    public async Task<IActionResult> Resolve(
        long id,
        ResolveAlarmBody body,
        CancellationToken cancellationToken) =>
        await alarms.ResolveAsync(id, body.Notes, cancellationToken) ? NoContent() : NotFound();

    private static AlarmDto Map(Alarm a) => new()
    {
        Id = a.Id,
        AlarmId = a.AlarmId,
        AlarmType = a.AlarmType,
        Status = a.Status,
        Message = a.Message,
        GateCode = a.Gate?.Code,
        DocumentNumber = a.Document?.DocumentNumber,
        CycleId = a.GateCycle?.CycleId,
        Epc = a.Epc,
        Epcs = string.IsNullOrEmpty(a.EpcList)
            ? []
            : a.EpcList.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        RaisedAt = a.RaisedAt,
        ResolvedBy = a.ResolvedByUser?.DisplayName,
        ResolvedAt = a.ResolvedAt,
        ResolutionNotes = a.ResolutionNotes
    };
}

// --------------------------------------------------------------------- EPCs

public sealed record EpcTagDto
{
    public required long Id { get; init; }

    public required string Epc { get; init; }

    public string? ItemCode { get; init; }

    public string? ItemName { get; init; }

    public string? SerialNumber { get; init; }

    public string? CartonNumber { get; init; }

    public string? ProductCode { get; init; }

    public int UnitQuantity { get; init; }

    public required EpcStatus Status { get; init; }

    public bool IsActive { get; init; }

    public DateTimeOffset? LastMovementAt { get; init; }
}

/// <summary>What an import did: the rows, and any documents planned from them.</summary>
public sealed record EpcImportOutcome
{
    public required EpcImportResult Import { get; init; }

    public IReadOnlyList<DocumentSummaryDto> Documents { get; init; } = [];
}

[ApiController]
[Route("api/epcs")]
[Authorize]
public sealed class EpcsController(
    IEpcImportService import,
    IDocumentService documents,
    IWarehouseDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] EpcStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.EpcTags.AsNoTracking().Include(t => t.Product).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = Epc.Normalize(search);

            query = query.Where(t => t.Epc.Contains(term)
                                  || (t.ItemCode != null && t.ItemCode.Contains(search))
                                  || (t.CartonNumber != null && t.CartonNumber.Contains(search)));
        }

        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }

        var total = await query.CountAsync(cancellationToken);
        var size = Math.Clamp(pageSize, 1, 1000);
        var index = Math.Max(1, page);

        var rows = await query
            .OrderBy(t => t.Epc)
            .Skip((index - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            items = rows.Select(t => new EpcTagDto
            {
                Id = t.Id,
                Epc = t.Epc,
                ItemCode = t.ItemCode,
                ItemName = t.ItemName,
                SerialNumber = t.SerialNumber,
                CartonNumber = t.CartonNumber,
                ProductCode = t.Product?.Code,
                UnitQuantity = t.UnitQuantity,
                Status = t.Status,
                IsActive = t.IsActive,
                LastMovementAt = t.LastMovementAt
            }).ToList(),
            total,
            page = index,
            pageSize = size
        });
    }

    /// <summary>
    /// Imports EPCs from a CSV file (§44).
    /// </summary>
    /// <remarks>
    /// Expected headers: Epc, ItemCode, ItemName, SerialNumber, CartonNumber,
    /// ProductCode, Description, UnitQuantity, Status. Only Epc is required.
    /// Invalid and duplicate rows are reported per line and nothing is
    /// half-imported.
    /// </remarks>
    [HttpPost("import")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [ProducesResponseType<EpcImportOutcome>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EpcImportOutcome>> Import(
        IFormFile file,
        [FromQuery] bool updateExisting = false,
        [FromQuery] bool generateDocuments = false,
        [FromQuery] DocumentType documentType = DocumentType.Inward,
        [FromQuery] int epcsPerDocument = 0,
        [FromQuery] string? gateCode = null,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "No file supplied",
                Detail = "Attach a CSV file in the 'file' form field.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        List<string> imported;
        EpcImportResult result;

        await using (var stream = file.OpenReadStream())
        {
            result = await import.ImportCsvAsync(stream, updateExisting, cancellationToken);
        }

        if (!generateDocuments || result.Imported + result.Updated == 0)
        {
            return Ok(new EpcImportOutcome { Import = result });
        }

        // Plan documents from exactly what the file brought in, in the order it
        // brought it. Re-reading the whole catalogue would sweep up rolls the
        // operator never mentioned.
        await using (var replay = file.OpenReadStream())
        {
            imported = await ReadEpcColumnAsync(replay, cancellationToken);
        }

        var eligible = await db.EpcTags
            .AsNoTracking()
            .Where(t => imported.Contains(t.Epc) && t.IsActive)
            .Where(t => documentType == DocumentType.Outward
                ? t.Status == EpcStatus.InStock
                : t.Status != EpcStatus.InStock)
            .OrderBy(t => t.ItemCode)
            .ThenBy(t => t.Epc)
            .Select(t => t.Epc)
            .ToListAsync(cancellationToken);

        var size = epcsPerDocument > 0 ? epcsPerDocument : eligible.Count;
        var created = new List<DocumentSummaryDto>();

        for (var offset = 0; offset < eligible.Count; offset += size)
        {
            var chunk = eligible.Skip(offset).Take(size).ToList();

            created.Add(await documents.CreateAsync(documentType, new CreateDocumentRequest
            {
                Epcs = chunk,
                GateCode = gateCode,
                Reference = $"Imported {file.FileName}",
                Notes = $"Generated automatically from {file.FileName}"
            }, cancellationToken));
        }

        return Ok(new EpcImportOutcome { Import = result, Documents = created });
    }

    /// <summary>Re-reads just the EPC column, so planning uses the file's own order.</summary>
    private static async Task<List<string>> ReadEpcColumnAsync(Stream stream, CancellationToken cancellationToken)
    {
        var epcs = new List<string>();

        using var reader = new StreamReader(stream);
        var header = await reader.ReadLineAsync(cancellationToken);

        if (header is null)
        {
            return epcs;
        }

        var columns = header.Split(',');
        var index = Array.FindIndex(columns, c => c.Trim().Trim('"')
            .Equals("Epc", StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            index = 0;
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var cells = line.Split(',');

            if (index < cells.Length)
            {
                var epc = Epc.Normalize(cells[index].Trim().Trim('"'));

                if (epc.Length > 0)
                {
                    epcs.Add(epc);
                }
            }
        }

        return epcs;
    }
}

// ---------------------------------------------------------------- dashboard

public sealed record DashboardDto
{
    public int ActiveGates { get; init; }

    public int OnlineReaders { get; init; }

    public int OfflineReaders { get; init; }

    public int TodayInward { get; init; }

    public int TodayOutward { get; init; }

    public int PendingDocuments { get; init; }

    public int CompletedDocuments { get; init; }

    public int ActiveAlarms { get; init; }

    public int UnknownEpcsToday { get; init; }

    public int TotalEpcs { get; init; }

    public int EpcsInStock { get; init; }
}

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController(IWarehouseDbContext db, IClock clock) : ControllerBase
{
    /// <summary>Headline counters for the admin dashboard (§41).</summary>
    [HttpGet]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardDto>> Get(CancellationToken cancellationToken)
    {
        var startOfDay = new DateTimeOffset(clock.UtcNow.Date, TimeSpan.Zero);

        return Ok(new DashboardDto
        {
            ActiveGates = await db.Gates.CountAsync(g => g.IsActive, cancellationToken),
            OnlineReaders = await db.Readers.CountAsync(r => r.IsActive && r.IsOnline, cancellationToken),
            OfflineReaders = await db.Readers.CountAsync(r => r.IsActive && !r.IsOnline, cancellationToken),

            TodayInward = await db.Documents.CountAsync(
                d => d.Type == DocumentType.Inward && d.CreatedAt >= startOfDay, cancellationToken),

            TodayOutward = await db.Documents.CountAsync(
                d => d.Type == DocumentType.Outward && d.CreatedAt >= startOfDay, cancellationToken),

            PendingDocuments = await db.Documents.CountAsync(
                d => d.Status == DocumentStatus.Released || d.Status == DocumentStatus.InProgress,
                cancellationToken),

            CompletedDocuments = await db.Documents.CountAsync(
                d => d.Status == DocumentStatus.Completed && d.CompletedAt >= startOfDay, cancellationToken),

            ActiveAlarms = await db.Alarms.CountAsync(a => a.Status != AlarmStatus.Resolved, cancellationToken),

            UnknownEpcsToday = await db.GateCycleEpcs.CountAsync(
                e => e.Classification == EpcClassification.Unknown && e.FirstSeenAt >= startOfDay,
                cancellationToken),

            TotalEpcs = await db.EpcTags.CountAsync(t => t.IsActive, cancellationToken),
            EpcsInStock = await db.EpcTags.CountAsync(t => t.Status == EpcStatus.InStock, cancellationToken)
        });
    }

    /// <summary>Recent audit entries, newest first (§32).</summary>
    [HttpGet("audit")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<IActionResult> Audit(
        [FromQuery] AuditAction? action,
        [FromQuery] string? documentNumber,
        [FromQuery] string? epc,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (action is { } a)
        {
            query = query.Where(l => l.Action == a);
        }

        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            query = query.Where(l => l.DocumentNumber == documentNumber);
        }

        if (!string.IsNullOrWhiteSpace(epc))
        {
            var normalised = Epc.Normalize(epc);
            query = query.Where(l => l.Epc == normalised);
        }

        var rows = await query
            .OrderByDescending(l => l.OccurredAt)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }
}
