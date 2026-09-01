namespace Warehouse.Domain.Entities;

/// <summary>Named role. Seeded with Administrator, Supervisor and Operator (§33).</summary>
public class Role
{
    public int Id { get; set; }

    /// <summary>Machine name used in <c>[Authorize(Roles = ...)]</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
}

public static class RoleNames
{
    public const string Administrator = "Administrator";
    public const string Supervisor = "Supervisor";
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = [Administrator, Supervisor, Operator];
}

public class User
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>BCrypt hash. No plaintext credential is ever persisted or logged.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Set when seeding a bootstrap account; forces a change on first login.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedOutUntil { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
}

public class UserRole
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
}
