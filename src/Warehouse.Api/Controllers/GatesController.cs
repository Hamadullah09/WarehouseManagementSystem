using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Gates;
using Warehouse.Domain;
using Warehouse.Domain.Entities;
using Warehouse.Domain.Gates;

namespace Warehouse.Api.Controllers;

public sealed record GateCycleDto
{
    public required long Id { get; init; }

    public required string CycleId { get; init; }

    public required string GateCode { get; init; }

    public string? DocumentNumber { get; init; }

    public required GateCycleStatus Status { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int ExpectedEpcCount { get; init; }

    public int DetectedEpcCount { get; init; }

    public int RawReadCount { get; init; }

    public int UnknownEpcCount { get; init; }

    public int UnexpectedEpcCount { get; init; }

    public int MissingEpcCount { get; init; }

    public ValidationOutcome? ValidationResult { get; init; }

    public string? ValidationSummary { get; init; }

    public bool InventoryCommitted { get; init; }

    public bool ReaderHealthy { get; init; }
}

public sealed record GateCycleEpcDto
{
    public required string Epc { get; init; }

    public required EpcClassification Classification { get; init; }

    public int ReadCount { get; init; }

    public double? PeakRssi { get; init; }

    public int? Antenna { get; init; }

    public DateTimeOffset? FirstSeenAt { get; init; }
}

[ApiController]
[Route("api/gates")]
[Authorize]
public sealed class GatesController(
    IGateCycleService gates,
    IWarehouseDbContext db) : ControllerBase
{
    /// <summary>Live snapshot of every configured gate.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<GateSnapshot>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GateSnapshot>>> List(CancellationToken cancellationToken) =>
        Ok(await gates.GetAllSnapshotsAsync(cancellationToken));

    /// <summary>
    /// Everything the gate display needs in one call. Used on load and as the
    /// fallback if the real-time channel drops (§19, §20).
    /// </summary>
    [HttpGet("{gateCode}/status")]
    [ProducesResponseType<GateSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GateSnapshot>> Status(string gateCode, CancellationToken cancellationToken)
    {
        var snapshot = await gates.GetSnapshotAsync(gateCode, cancellationToken);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    /// <summary>Arms the gate so the next input edge opens a cycle.</summary>
    [HttpPost("{gateCode}/start")]
    [ProducesResponseType<GateSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GateSnapshot>> Start(string gateCode, CancellationToken cancellationToken) =>
        Ok(await gates.ArmAsync(gateCode, cancellationToken));

    /// <summary>Disarms the gate. A cycle already running is closed and validated.</summary>
    [HttpPost("{gateCode}/stop")]
    [ProducesResponseType<GateSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GateSnapshot>> Stop(string gateCode, CancellationToken cancellationToken) =>
        Ok(await gates.DisarmAsync(gateCode, cancellationToken));

    /// <summary>Transitions this gate's state machine currently permits. Drives the admin UI.</summary>
    [HttpGet("{gateCode}/transitions")]
    public async Task<ActionResult<IReadOnlyList<string>>> Transitions(
        string gateCode,
        CancellationToken cancellationToken)
    {
        var snapshot = await gates.GetSnapshotAsync(gateCode, cancellationToken);

        if (snapshot is null)
        {
            return NotFound();
        }

        return Ok(GateStateMachine.AllowedTriggers(snapshot.State).Select(t => t.ToString()).ToList());
    }

    /// <summary>Historical cycles for a gate, newest first.</summary>
    [HttpGet("{gateCode}/cycles")]
    [ProducesResponseType<IReadOnlyList<GateCycleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GateCycleDto>>> Cycles(
        string gateCode,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var cycles = await db.GateCycles
            .AsNoTracking()
            .Include(c => c.Gate)
            .Include(c => c.Document)
            .Where(c => c.Gate.Code == gateCode)
            .OrderByDescending(c => c.StartedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

        return Ok(cycles.Select(Map).ToList());
    }

    /// <summary>Every EPC recorded against one cycle, with its classification.</summary>
    [HttpGet("cycles/{cycleId}/epcs")]
    [ProducesResponseType<IReadOnlyList<GateCycleEpcDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GateCycleEpcDto>>> CycleEpcs(
        string cycleId,
        CancellationToken cancellationToken)
    {
        var epcs = await db.GateCycleEpcs
            .AsNoTracking()
            .Where(e => e.GateCycle.CycleId == cycleId)
            .OrderBy(e => e.Classification)
            .ThenBy(e => e.Epc)
            .Select(e => new GateCycleEpcDto
            {
                Epc = e.Epc,
                Classification = e.Classification,
                ReadCount = e.ReadCount,
                PeakRssi = e.PeakRssi,
                Antenna = e.Antenna,
                FirstSeenAt = e.FirstSeenAt
            })
            .ToListAsync(cancellationToken);

        return Ok(epcs);
    }

    private static GateCycleDto Map(GateCycle c) => new()
    {
        Id = c.Id,
        CycleId = c.CycleId,
        GateCode = c.Gate.Code,
        DocumentNumber = c.Document?.DocumentNumber,
        Status = c.Status,
        StartedAt = c.StartedAt,
        CompletedAt = c.CompletedAt,
        ExpectedEpcCount = c.ExpectedEpcCount,
        DetectedEpcCount = c.DetectedEpcCount,
        RawReadCount = c.RawReadCount,
        UnknownEpcCount = c.UnknownEpcCount,
        UnexpectedEpcCount = c.UnexpectedEpcCount,
        MissingEpcCount = c.MissingEpcCount,
        ValidationResult = c.ValidationResult,
        ValidationSummary = c.ValidationSummary,
        InventoryCommitted = c.InventoryCommitted,
        ReaderHealthy = c.ReaderHealthy
    };
}
