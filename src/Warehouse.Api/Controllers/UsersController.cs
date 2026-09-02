using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Documents;
using Warehouse.Application.Options;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Api.Controllers;

public sealed record UserDto
{
    public required int Id { get; init; }

    public required string UserName { get; init; }

    public required string DisplayName { get; init; }

    public string? Email { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public bool IsActive { get; init; }

    public bool MustChangePassword { get; init; }

    /// <summary>True while a lockout is in force.</summary>
    public bool IsLockedOut { get; init; }

    /// <summary>Set when the user has asked for a password reset.</summary>
    public bool ResetRequested { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record CreateUserRequest
{
    [Required]
    [MaxLength(64)]
    public string UserName { get; init; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string DisplayName { get; init; } = string.Empty;

    [MaxLength(256)]
    public string? Email { get; init; }

    [Required]
    public string Password { get; init; } = string.Empty;

    /// <summary>One or more of Administrator, Supervisor, Operator.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [RoleNames.Operator];

    /// <summary>Force a password change at first sign-in. On by default.</summary>
    public bool MustChangePassword { get; init; } = true;
}

public sealed record UpdateUserRequest
{
    [MaxLength(128)]
    public string? DisplayName { get; init; }

    [MaxLength(256)]
    public string? Email { get; init; }

    public IReadOnlyList<string>? Roles { get; init; }

    public bool? IsActive { get; init; }
}

public sealed record ResetPasswordRequest
{
    [Required]
    public string NewPassword { get; init; } = string.Empty;

    /// <summary>Make the user choose their own at next sign-in. On by default.</summary>
    public bool MustChangePassword { get; init; } = true;
}

/// <summary>
/// User administration.
/// </summary>
/// <remarks>
/// Every movement in this system is attributed to a person, so the account
/// list is operational data, not an afterthought. Deactivating is preferred to
/// deleting: an account that has signed gate cycles cannot be removed without
/// leaving an audit trail that points at nobody.
/// </remarks>
[ApiController]
[Route("api/users")]
[Authorize(Roles = RoleNames.Administrator)]
public sealed class UsersController(
    IWarehouseDbContext db,
    IClock clock,
    ICurrentUser currentUser,
    IAuditService audit,
    IOptionsMonitor<SecurityOptions> security) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var users = await db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.UserName)
            .ToListAsync(cancellationToken);

        return Ok(users.Select(u => Map(u, now)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        return user is null ? NotFound() : Ok(Map(user, clock.UtcNow));
    }

    /// <summary>Roles that can be assigned. Read from the database, not a constant.</summary>
    [HttpGet("roles")]
    public async Task<IActionResult> Roles(CancellationToken cancellationToken) =>
        Ok(await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new { r.Name, r.Description })
            .ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var userName = request.UserName.Trim();

        if (await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken))
        {
            throw new WarehouseValidationException($"User name '{userName}' is already taken.");
        }

        var minimum = security.CurrentValue.MinimumPasswordLength;

        if (request.Password.Length < minimum)
        {
            throw new WarehouseValidationException(
                $"The password must be at least {minimum} characters.");
        }

        var roles = await ResolveRolesAsync(request.Roles, cancellationToken);

        var user = new User
        {
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? userName
                : request.DisplayName.Trim(),
            Email = request.Email?.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12),
            IsActive = true,
            MustChangePassword = request.MustChangePassword,
            CreatedAt = clock.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var role in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            Result = "USER_CREATED",
            Details = $"{user.UserName} ({string.Join(", ", roles.Select(r => r.Name))})"
        });

        await db.SaveChangesAsync(cancellationToken);

        var created = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstAsync(u => u.Id == user.Id, cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, Map(created, clock.UtcNow));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        if (request.DisplayName is { Length: > 0 })
        {
            user.DisplayName = request.DisplayName.Trim();
        }

        if (request.Email is not null)
        {
            user.Email = request.Email.Trim();
        }

        if (request.IsActive is { } active)
        {
            // Locking yourself out of the only administrator account is a
            // support call nobody enjoys.
            if (!active && user.Id == currentUser.UserId)
            {
                throw new WarehouseValidationException("You cannot deactivate your own account.");
            }

            user.IsActive = active;

            if (active)
            {
                user.LockedOutUntil = null;
                user.FailedLoginCount = 0;
            }
        }

        if (request.Roles is { Count: > 0 })
        {
            var roles = await ResolveRolesAsync(request.Roles, cancellationToken);

            if (user.Id == currentUser.UserId
                && !roles.Any(r => r.Name == RoleNames.Administrator))
            {
                throw new WarehouseValidationException(
                    "You cannot remove the Administrator role from your own account.");
            }

            db.UserRoles.RemoveRange(user.UserRoles);

            foreach (var role in roles)
            {
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            }
        }

        user.UpdatedAt = clock.UtcNow;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            Result = "USER_UPDATED",
            Details = user.UserName
        });

        await db.SaveChangesAsync(cancellationToken);

        var updated = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstAsync(u => u.Id == id, cancellationToken);

        return Ok(Map(updated, clock.UtcNow));
    }

    /// <summary>Sets a new password for someone who has forgotten theirs.</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        int id,
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        var minimum = security.CurrentValue.MinimumPasswordLength;

        if (request.NewPassword.Length < minimum)
        {
            throw new WarehouseValidationException(
                $"The password must be at least {minimum} characters.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        user.MustChangePassword = request.MustChangePassword;
        user.FailedLoginCount = 0;
        user.LockedOutUntil = null;
        user.PasswordResetRequestedAt = null;
        user.UpdatedAt = clock.UtcNow;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            Result = "PASSWORD_RESET",
            Details = user.UserName
        });

        await db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Deactivates, or deletes when the account has never been used.
    /// </summary>
    /// <remarks>
    /// An account that has signed documents or gate cycles is deactivated
    /// rather than removed, so the audit trail keeps naming a real person.
    /// </remarks>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        if (user.Id == currentUser.UserId)
        {
            throw new WarehouseValidationException("You cannot remove your own account.");
        }

        var hasHistory = await db.Documents.AnyAsync(d => d.UserId == id, cancellationToken)
            || await db.AuditLogs.AnyAsync(a => a.UserId == id, cancellationToken);

        if (hasHistory)
        {
            user.IsActive = false;
            user.UpdatedAt = clock.UtcNow;

            audit.Enlist(new AuditEntry
            {
                Action = AuditAction.SettingChanged,
                Result = "USER_DEACTIVATED",
                Details = $"{user.UserName} kept for its history"
            });

            await db.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                deactivated = true,
                message = $"{user.DisplayName} has history in the system, so the account was "
                        + "deactivated rather than deleted."
            });
        }

        db.UserRoles.RemoveRange(user.UserRoles);
        db.Users.Remove(user);

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.SettingChanged,
            Result = "USER_DELETED",
            Details = user.UserName
        });

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new { deactivated = false, message = $"{user.DisplayName} was removed." });
    }

    private async Task<List<Role>> ResolveRolesAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();

        if (wanted.Count == 0)
        {
            wanted.Add(RoleNames.Operator);
        }

        var roles = await db.Roles.Where(r => wanted.Contains(r.Name)).ToListAsync(cancellationToken);
        var missing = wanted.Except(roles.Select(r => r.Name)).ToList();

        if (missing.Count > 0)
        {
            throw new WarehouseValidationException(
                $"Unknown role(s): {string.Join(", ", missing)}.", missing);
        }

        return roles;
    }

    private static UserDto Map(User u, DateTimeOffset now) => new()
    {
        Id = u.Id,
        UserName = u.UserName,
        DisplayName = u.DisplayName,
        Email = u.Email,
        Roles = u.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToList(),
        IsActive = u.IsActive,
        MustChangePassword = u.MustChangePassword,
        IsLockedOut = u.LockedOutUntil is { } until && until > now,
        ResetRequested = u.PasswordResetRequestedAt is not null,
        LastLoginAt = u.LastLoginAt,
        CreatedAt = u.CreatedAt
    };
}
