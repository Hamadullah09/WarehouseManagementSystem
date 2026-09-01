using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Api.Services;

/// <summary>JWT signing and lifetime settings. The key never lives in source (§33).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "warehouse-gate";

    [Required]
    public string Audience { get; set; } = "warehouse-gate";

    /// <summary>
    /// Signing key. Must be at least 32 bytes. Supply it through an
    /// environment variable, user secret or key vault, never appsettings.json
    /// in source control.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int LifetimeMinutes { get; set; } = 60;
}

/// <summary>The authenticated caller, resolved from the request's claims.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public int? UserId =>
        int.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    public string? UserName => accessor.HttpContext?.User.Identity?.Name;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool IsInRole(string role) => accessor.HttpContext?.User.IsInRole(role) ?? false;
}

/// <summary>Issues bearer tokens for authenticated users.</summary>
public sealed class TokenService(IOptions<JwtOptions> options)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(AuthenticatedUser user)
    {
        var config = options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(config.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new("displayName", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.SigningKey));

        var token = new JwtSecurityToken(
            issuer: config.Issuer,
            audience: config.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
