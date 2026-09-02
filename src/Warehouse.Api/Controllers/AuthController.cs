using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Warehouse.Api.Services;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Api.Controllers;

public sealed record LoginRequest
{
    [Required]
    [MaxLength(64)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Password { get; init; } = string.Empty;
}

public sealed record LoginResponse
{
    public required string Token { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string UserName { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required bool MustChangePassword { get; init; }
}

public sealed record ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required]
    // Length is enforced by AuthService against the configured policy, not by
    // a constant here. Two independent minimums drift apart, and the model
    // binder's message would contradict the service's.
    [MinLength(1)]
    [MaxLength(256)]
    public string NewPassword { get; init; } = string.Empty;
}

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IAuthService auth,
    TokenService tokens,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Exchanges credentials for a bearer token.</summary>
    /// <remarks>
    /// Rate limited: this is the one anonymous endpoint that accepts secrets,
    /// so it is the one worth throttling hardest (§33).
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var user = await auth.AuthenticateAsync(request.UserName, request.Password, cancellationToken);

        if (user is null)
        {
            // Deliberately uniform: never reveal whether the account exists,
            // is disabled, or is locked out.
            return Unauthorized(new ProblemDetails
            {
                Title = "Authentication failed",
                Detail = "The user name or password is incorrect.",
                Status = StatusCodes.Status401Unauthorized
            });
        }

        var (token, expiresAt) = tokens.Issue(user);

        return Ok(new LoginResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Roles = user.Roles,
            MustChangePassword = user.MustChangePassword
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        var changed = await auth.ChangePasswordAsync(
            userId, request.CurrentPassword, request.NewPassword, cancellationToken);

        return changed
            ? NoContent()
            : BadRequest(new ProblemDetails
            {
                Title = "Password not changed",
                Detail = "The current password is incorrect.",
                Status = StatusCodes.Status400BadRequest
            });
    }

    /// <summary>Returns the caller's identity, for the SPA to render its shell.</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        userId = currentUser.UserId,
        userName = currentUser.UserName,
        roles = User.Claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList()
    });
}
