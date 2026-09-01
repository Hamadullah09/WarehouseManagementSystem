using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Domain.Entities;
using Warehouse.Rfid.Abstractions;
using Warehouse.Rfid.Simulation;

namespace Warehouse.Api.Controllers;

public sealed record EmitTagsBody
{
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> Epcs { get; init; } = [];

    /// <summary>
    /// Times to report each EPC. Values above 1 exercise the deduplication
    /// path, which is the point of having it (§12).
    /// </summary>
    [Range(1, 1000)]
    public int Repeats { get; init; } = 1;
}

public sealed record SimulateCycleBody
{
    [Required]
    [MinLength(0)]
    public IReadOnlyList<string> Epcs { get; init; } = [];

    [Range(1, 1000)]
    public int Repeats { get; init; } = 3;

    /// <summary>Pause between the input going active and clearing, milliseconds.</summary>
    [Range(0, 60_000)]
    public int HoldMs { get; init; } = 200;
}

/// <summary>
/// Drives simulated readers for development and end-to-end testing (§40).
/// </summary>
/// <remarks>
/// Every action here fabricates hardware events, so the controller is
/// registered only in the Development environment and every endpoint re-checks
/// that the target reader is genuinely a simulator. A U300 reader can never be
/// driven from here, and in production these routes do not exist at all.
/// </remarks>
[ApiController]
[Route("api/simulation")]
[Authorize(Roles = RoleNames.Administrator)]
public sealed class SimulationController(
    IRfidReaderRegistry registry,
    IHostEnvironment environment,
    ILogger<SimulationController> logger) : ControllerBase
{
    /// <summary>Lists the simulated readers available to drive.</summary>
    [HttpGet("readers")]
    public ActionResult<IEnumerable<object>> Readers()
    {
        if (Guard() is { } guard)
        {
            return guard;
        }

        return Ok(registry.All
            .OfType<ISimulatedReader>()
            .Select(r => new { r.ReaderId, r.GateId, outputs = r.Outputs })
            .ToList());
    }

    /// <summary>Drives the gate input active: the reader starts a cycle.</summary>
    [HttpPost("readers/{readerId}/gpio-on")]
    public async Task<IActionResult> GpioOn(string readerId, CancellationToken cancellationToken)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        logger.LogWarning("SIMULATION: gate input ON for reader {ReaderId}", readerId);
        await reader.GpioOnAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>Clears the gate input: the cycle closes and validation runs.</summary>
    [HttpPost("readers/{readerId}/gpio-off")]
    public async Task<IActionResult> GpioOff(string readerId, CancellationToken cancellationToken)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        logger.LogWarning("SIMULATION: gate input OFF for reader {ReaderId}", readerId);
        await reader.GpioOffAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>Emits tag reads as if the antenna had seen them.</summary>
    [HttpPost("readers/{readerId}/tags")]
    public async Task<IActionResult> EmitTags(
        string readerId,
        EmitTagsBody body,
        CancellationToken cancellationToken)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        logger.LogWarning(
            "SIMULATION: emitting {Count} EPC(s) x{Repeats} on reader {ReaderId}",
            body.Epcs.Count, body.Repeats, readerId);

        await reader.EmitTagsAsync(body.Epcs, body.Repeats, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Runs a whole gate pass: input active, tags, input clear. The convenient
    /// way to exercise the full pipeline from a script or a test.
    /// </summary>
    [HttpPost("readers/{readerId}/cycle")]
    public async Task<IActionResult> Cycle(
        string readerId,
        SimulateCycleBody body,
        CancellationToken cancellationToken)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        logger.LogWarning("SIMULATION: full cycle on reader {ReaderId}", readerId);

        await reader.GpioOnAsync(cancellationToken);

        if (body.Epcs.Count > 0)
        {
            await reader.EmitTagsAsync(body.Epcs, body.Repeats, cancellationToken);
        }

        if (body.HoldMs > 0)
        {
            await Task.Delay(body.HoldMs, cancellationToken);
        }

        await reader.GpioOffAsync(cancellationToken);

        return Accepted(new { readerId, emitted = body.Epcs.Count });
    }

    /// <summary>Simulates the reader dropping off the network (§29).</summary>
    [HttpPost("readers/{readerId}/disconnect")]
    public async Task<IActionResult> Disconnect(
        string readerId,
        [FromQuery] string reason = "Simulated network failure",
        CancellationToken cancellationToken = default)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        await reader.DisconnectAsync(reason, cancellationToken);

        return NoContent();
    }

    /// <summary>Simulates the reader coming back (§30).</summary>
    [HttpPost("readers/{readerId}/reconnect")]
    public async Task<IActionResult> Reconnect(string readerId, CancellationToken cancellationToken)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        await reader.ReconnectAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>Simulates an SDK-level failure during a cycle (§37).</summary>
    [HttpPost("readers/{readerId}/error")]
    public async Task<IActionResult> RaiseError(
        string readerId,
        [FromQuery] string operation = "startInventoryTag",
        [FromQuery] string message = "Simulated reader fault",
        CancellationToken cancellationToken = default)
    {
        if (Resolve(readerId, out var reader) is { } failure)
        {
            return failure;
        }

        await reader.RaiseErrorAsync(operation, message, cancellationToken);

        return NoContent();
    }

    private ObjectResult? Guard() => environment.IsDevelopment()
        ? null
        : NotFound(new ProblemDetails
        {
            Title = "Not available",
            Detail = "Simulation endpoints exist only in the Development environment.",
            Status = StatusCodes.Status404NotFound
        });

    private ObjectResult? Resolve(string readerId, out ISimulatedReader reader)
    {
        reader = null!;

        if (Guard() is { } guard)
        {
            return guard;
        }

        if (!registry.TryGet(readerId, out var found) || found is not ISimulatedReader simulated)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No simulated reader",
                Detail = $"Reader '{readerId}' is not registered as a simulator and cannot be driven from here.",
                Status = StatusCodes.Status404NotFound
            });
        }

        reader = simulated;

        return null;
    }
}
