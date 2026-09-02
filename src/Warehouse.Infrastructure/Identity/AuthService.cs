using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Options;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Infrastructure.Identity;

public sealed record AuthenticatedUser(
    int Id,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool MustChangePassword);

public interface IAuthService
{
    /// <summary>Verifies credentials. Returns null on any failure, without saying which.</summary>
    Task<AuthenticatedUser?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);

    Task<bool> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Credential verification with lockout (§33).
/// </summary>
/// <remarks>
/// Passwords are stored as BCrypt hashes and never logged, echoed or returned.
/// Failures are deliberately indistinguishable to the caller -- unknown user,
/// wrong password and locked account all yield null -- so the endpoint cannot
/// be used to enumerate accounts. The audit trail records the distinction for
/// operators, since that is where it belongs.
/// </remarks>
public sealed class AuthService(
    IWarehouseDbContext db,
    IClock clock,
    IAuditService audit,
    IOptionsMonitor<SecurityOptions> security,
    ILogger<AuthService> logger) : IAuthService
{

    public async Task<AuthenticatedUser?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            // Hash anyway so a missing account is not faster than a wrong password.
            BCrypt.Net.BCrypt.Verify(password, BCrypt.Net.BCrypt.HashPassword("timing-equaliser"));

            await FailAsync(userName, "unknown user", cancellationToken).ConfigureAwait(false);

            return null;
        }

        var now = clock.UtcNow;

        if (!user.IsActive)
        {
            await FailAsync(userName, "account disabled", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (user.LockedOutUntil is { } until && until > now)
        {
            await FailAsync(userName, "account locked out", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;

            if (user.FailedLoginCount >= security.CurrentValue.MaxFailedAttempts)
            {
                user.LockedOutUntil = now.AddMinutes(security.CurrentValue.LockoutMinutes);
                user.FailedLoginCount = 0;

                logger.LogWarning(
                    "User {UserName} locked out until {Until} after repeated failures", userName, user.LockedOutUntil);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await FailAsync(userName, "invalid password", cancellationToken).ConfigureAwait(false);

            return null;
        }

        user.FailedLoginCount = 0;
        user.LockedOutUntil = null;
        user.LastLoginAt = now;

        audit.Enlist(new AuditEntry
        {
            Action = AuditAction.UserLoggedIn,
            UserId = user.Id,
            UserName = user.UserName,
            Result = "SUCCESS"
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AuthenticatedUser(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.UserRoles.Select(ur => ur.Role.Name).ToList(),
            user.MustChangePassword);
    }

    public async Task<bool> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var minimum = security.CurrentValue.MinimumPasswordLength;

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < minimum)
        {
            throw new ArgumentException(
                $"The new password must be at least {minimum} characters.", nameof(newPassword));
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        user.MustChangePassword = false;
        user.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Password changed for user {UserName}", user.UserName);

        return true;
    }

    private Task FailAsync(string userName, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Login failed for {UserName}: {Reason}", userName, reason);

        return audit.WriteAsync(new AuditEntry
        {
            Action = AuditAction.UserLoginFailed,
            UserName = userName,
            Result = "FAILED",
            Details = reason
        }, cancellationToken);
    }
}
