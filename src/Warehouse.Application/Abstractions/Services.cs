using Warehouse.Domain;

namespace Warehouse.Application.Abstractions;

/// <summary>Injectable clock. Nothing in the application layer reads DateTime directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Allocates document and gate-cycle numbers (§5).
/// </summary>
/// <remarks>
/// Database-backed and concurrency-safe by construction. Never generated in
/// the browser and never derived from a client-supplied value.
/// </remarks>
public interface INumberGenerator
{
    /// <summary>Returns the next document number, e.g. "IN-2026-000001".</summary>
    Task<string> NextDocumentNumberAsync(DocumentType type, CancellationToken cancellationToken = default);

    /// <summary>Returns the next gate cycle id, e.g. "GC-2026-000001".</summary>
    Task<string> NextCycleIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the next alarm id, e.g. "AL-2026-000001".</summary>
    Task<string> NextAlarmIdAsync(CancellationToken cancellationToken = default);
}

/// <summary>The authenticated caller, or an unauthenticated placeholder.</summary>
public interface ICurrentUser
{
    int? UserId { get; }
    string? UserName { get; }
    string? IpAddress { get; }
    bool IsInRole(string role);
}
