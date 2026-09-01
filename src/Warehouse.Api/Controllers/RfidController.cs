using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Domain.Entities;
using Warehouse.Rfid.Abstractions;

namespace Warehouse.Api.Controllers;

public sealed record ReaderDto
{
    public required string ReaderId { get; init; }

    public required string Name { get; init; }

    public required string GateCode { get; init; }

    public string? IpAddress { get; init; }

    public int? Port { get; init; }

    public required string Model { get; init; }

    public required ReaderConnectionState State { get; init; }

    public bool IsOnline { get; init; }

    public bool IsInventorying { get; init; }

    public string? FirmwareVersion { get; init; }

    public string? HardwareVersion { get; init; }

    public double? TemperatureCelsius { get; init; }

    public IReadOnlyList<int> Antennas { get; init; } = [];

    public IReadOnlyList<string> Gpio { get; init; } = [];

    public DateTimeOffset? LastSeenAt { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    public string? LastError { get; init; }
}

public sealed record SetGpoBody
{
    /// <summary>Pin name: GPO1..GPO4, WiegandData0, WiegandData1.</summary>
    public required string Pin { get; init; }

    public required bool High { get; init; }
}

[ApiController]
[Route("api/rfid")]
[Authorize]
public sealed class RfidController(
    IRfidReaderRegistry registry,
    IWarehouseDbContext db,
    ILogger<RfidController> logger) : ControllerBase
{
    /// <summary>Every configured reader with its live state (§41).</summary>
    [HttpGet("readers")]
    [ProducesResponseType<IReadOnlyList<ReaderDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReaderDto>>> Readers(CancellationToken cancellationToken)
    {
        var rows = await db.Readers
            .AsNoTracking()
            .Include(r => r.Gate)
            .Where(r => r.IsActive)
            .ToListAsync(cancellationToken);

        var result = new List<ReaderDto>(rows.Count);

        foreach (var row in rows)
        {
            registry.TryGet(row.ReaderId, out var reader);

            var status = reader is null
                ? null
                : await SafeStatusAsync(reader, cancellationToken);

            result.Add(Map(row, reader?.State ?? ReaderConnectionState.Disconnected, status));
        }

        return Ok(result);
    }

    [HttpGet("readers/{readerId}/status")]
    [ProducesResponseType<ReaderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReaderDto>> Status(string readerId, CancellationToken cancellationToken)
    {
        var row = await db.Readers
            .AsNoTracking()
            .Include(r => r.Gate)
            .FirstOrDefaultAsync(r => r.ReaderId == readerId, cancellationToken);

        if (row is null)
        {
            return NotFound();
        }

        registry.TryGet(readerId, out var reader);

        var status = reader is null ? null : await SafeStatusAsync(reader, cancellationToken);

        return Ok(Map(row, reader?.State ?? ReaderConnectionState.Disconnected, status));
    }

    [HttpPost("readers/{readerId}/connect")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<IActionResult> Connect(string readerId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(readerId, out var reader))
        {
            return NotFound();
        }

        var connected = await reader.ConnectAsync(cancellationToken);

        return Ok(new { readerId, connected, state = reader.State.ToString() });
    }

    [HttpPost("readers/{readerId}/disconnect")]
    [Authorize(Roles = $"{RoleNames.Administrator},{RoleNames.Supervisor}")]
    public async Task<IActionResult> Disconnect(string readerId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(readerId, out var reader))
        {
            return NotFound();
        }

        await reader.DisconnectAsync(cancellationToken);

        return Ok(new { readerId, state = reader.State.ToString() });
    }

    /// <summary>Latest known digital input levels.</summary>
    [HttpGet("readers/{readerId}/gpio")]
    public async Task<IActionResult> ReadGpio(string readerId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(readerId, out var reader))
        {
            return NotFound();
        }

        var inputs = await reader.ReadGpioStateAsync(cancellationToken);

        return Ok(inputs.Select(i => new { pin = i.Pin.ToString(), high = i.High }));
    }

    /// <summary>
    /// Drives an output line. Administrator-only: these pins are wired to
    /// beacons, sounders and in some installations to a barrier relay.
    /// </summary>
    [HttpPost("readers/{readerId}/gpio")]
    [Authorize(Roles = RoleNames.Administrator)]
    public async Task<IActionResult> SetGpio(
        string readerId,
        SetGpoBody body,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(readerId, out var reader))
        {
            return NotFound();
        }

        if (!Enum.TryParse<GpoPin>(body.Pin, true, out var pin))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown output pin",
                Detail = $"'{body.Pin}' is not one of: {string.Join(", ", Enum.GetNames<GpoPin>())}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var ok = await reader.SetGpioOutputAsync([new GpoCommand(pin, body.High)], cancellationToken);

        logger.LogInformation(
            "Operator set {Pin}={High} on reader {ReaderId} (result {Result})",
            pin, body.High, readerId, ok);

        return Ok(new { readerId, pin = pin.ToString(), high = body.High, applied = ok });
    }

    private async Task<ReaderStatus?> SafeStatusAsync(IRfidReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.GetReaderStatusAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A reader that will not answer is a fact to display, not an error
            // that should fail the whole listing.
            logger.LogDebug(ex, "Status probe failed for reader {ReaderId}", reader.ReaderId);

            return null;
        }
    }

    private static ReaderDto Map(Reader row, ReaderConnectionState state, ReaderStatus? status) => new()
    {
        ReaderId = row.ReaderId,
        Name = row.Name,
        GateCode = row.Gate.Code,
        IpAddress = row.IpAddress,
        Port = row.Port,
        Model = row.Model,
        State = state,
        IsOnline = state == ReaderConnectionState.Connected,
        IsInventorying = status?.IsInventorying ?? row.IsInventorying,
        FirmwareVersion = status?.FirmwareVersion ?? row.FirmwareVersion,
        HardwareVersion = status?.HardwareVersion ?? row.HardwareVersion,
        TemperatureCelsius = status?.TemperatureCelsius ?? row.TemperatureCelsius,
        Antennas = status?.EnabledAntennas ?? [],
        Gpio = status?.Inputs.Select(i => i.ToString()).ToList() ?? [],
        LastSeenAt = status?.LastSeenAt ?? row.LastSeenAt,
        ConnectedAt = status?.ConnectedAt ?? row.ConnectedAt,
        LastError = status?.LastError ?? row.LastError
    };
}
