using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Application.Documents;
using Warehouse.Application.Gates;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Api.Controllers;

public sealed record CreateDocumentBody
{
    /// <summary>EPCs expected to cross the gate. Normalised and de-duplicated server-side.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> Epcs { get; init; } = [];

    public int? UserId { get; init; }

    /// <summary>Gate to release to. When supplied the document is created Released.</summary>
    [MaxLength(64)]
    public string? GateCode { get; init; }

    [MaxLength(128)]
    public string? Reference { get; init; }

    [MaxLength(2048)]
    public string? Notes { get; init; }

    public CreateDocumentRequest ToRequest() => new()
    {
        Epcs = Epcs,
        UserId = UserId,
        GateCode = GateCode,
        Reference = Reference,
        Notes = Notes
    };
}

public sealed record ReleaseDocumentBody
{
    [Required]
    [MaxLength(64)]
    public string GateCode { get; init; } = string.Empty;
}

public sealed record CancelDocumentBody
{
    [MaxLength(512)]
    public string? Reason { get; init; }
}

[ApiController]
[Route("api/documents")]
[Authorize]
public sealed class DocumentsController(IDocumentService documents) : ControllerBase
{
    /// <summary>Lists documents with filtering and paging.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<DocumentSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DocumentSummaryDto>>> List(
        [FromQuery] DocumentType? type,
        [FromQuery] DocumentStatus? status,
        [FromQuery] string? gateCode,
        [FromQuery] string? search,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await documents.ListAsync(new DocumentQuery
        {
            Type = type,
            Status = status,
            GateCode = gateCode,
            Search = search,
            From = from,
            To = to,
            Page = page,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<DocumentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDetailDto>> Get(int id, CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(id, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    /// <summary>Looks a document up by its number, e.g. IN-2026-000001.</summary>
    [HttpGet("by-number/{documentNumber}")]
    [ProducesResponseType<DocumentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDetailDto>> GetByNumber(
        string documentNumber,
        CancellationToken cancellationToken)
    {
        var document = await documents.GetByNumberAsync(documentNumber, cancellationToken);
        return document is null ? NotFound() : Ok(document);
    }

    /// <summary>Creates an INWARD document. The number is allocated by the database (§5).</summary>
    [HttpPost("inward")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    [ProducesResponseType<DocumentDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<DocumentDetailDto>> CreateInward(
        CreateDocumentBody body,
        CancellationToken cancellationToken) =>
        CreateAsync(DocumentType.Inward, body, cancellationToken);

    /// <summary>Creates an OUTWARD document.</summary>
    [HttpPost("outward")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    [ProducesResponseType<DocumentDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<DocumentDetailDto>> CreateOutward(
        CreateDocumentBody body,
        CancellationToken cancellationToken) =>
        CreateAsync(DocumentType.Outward, body, cancellationToken);

    /// <summary>Binds a document to a gate so the next gate cycle works against it.</summary>
    [HttpPost("{id:int}/release")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<ActionResult<DocumentDetailDto>> Release(
        int id,
        ReleaseDocumentBody body,
        CancellationToken cancellationToken) =>
        Ok(await documents.ReleaseAsync(id, body.GateCode, cancellationToken));

    [HttpPost("{id:int}/cancel")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<ActionResult<DocumentDetailDto>> Cancel(
        int id,
        CancelDocumentBody body,
        CancellationToken cancellationToken) =>
        Ok(await documents.CancelAsync(id, body.Reason, cancellationToken));

    /// <summary>
    /// Resets outstanding lines so the operator can run the gate again.
    /// EPCs already confirmed by a passed cycle stay committed (§15).
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<DocumentDetailDto>> Retry(int id, CancellationToken cancellationToken) =>
        Ok(await documents.RetryAsync(id, cancellationToken));

    private async Task<ActionResult<DocumentDetailDto>> CreateAsync(
        DocumentType type,
        CreateDocumentBody body,
        CancellationToken cancellationToken)
    {
        var document = await documents.CreateAsync(type, body.ToRequest(), cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = document.Id }, document);
    }

    /// <summary>
    /// Records a read session performed by an app running on the reader.
    /// </summary>
    /// <remarks>
    /// The device validates locally for instant operator feedback; this
    /// re-validates against the database and is the only path that moves stock.
    /// Re-posting the same sessionKey returns the original verdict rather than
    /// double-counting the load.
    /// </remarks>
    [HttpPost("{id:int}/scan-sessions")]
    [Authorize]
    public async Task<IActionResult> SubmitScanSession(
        int id,
        [FromBody] DeviceSessionRequest request,
        [FromServices] IDeviceSessionService sessions,
        CancellationToken cancellationToken)
        => Ok(await sessions.SubmitAsync(id, request, cancellationToken));
}
