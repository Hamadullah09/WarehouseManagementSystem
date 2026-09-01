using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Warehouse.Domain.Entities;

namespace Warehouse.Application.Abstractions;

/// <summary>
/// Persistence surface the application layer is allowed to see.
/// </summary>
/// <remarks>
/// The concrete <c>WarehouseDbContext</c> lives in Infrastructure with the
/// provider, migrations and mapping. Services depend on this so they can be
/// tested against the in-memory provider without dragging SQL Server in.
/// </remarks>
public interface IWarehouseDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<Product> Products { get; }
    DbSet<EpcTag> EpcTags { get; }
    DbSet<InventoryItem> Inventory { get; }
    DbSet<InventoryMovement> InventoryMovements { get; }
    DbSet<Gate> Gates { get; }
    DbSet<Reader> Readers { get; }
    DbSet<ReaderEvent> ReaderEvents { get; }
    DbSet<GpioEvent> GpioEvents { get; }
    DbSet<Document> Documents { get; }
    DbSet<DocumentItem> DocumentItems { get; }
    DbSet<GateCycle> GateCycles { get; }
    DbSet<GateCycleEpc> GateCycleEpcs { get; }
    DbSet<Alarm> Alarms { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<NumberSequence> NumberSequences { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a real database transaction. Returns null when the provider does
    /// not support transactions (the in-memory provider used in unit tests).
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ExecuteInTransactionAsync{T}"/> for anything that must
    /// be atomic. SQL Server is configured with a retrying execution strategy,
    /// which refuses user-initiated transactions unless they are wrapped in a
    /// retriable unit.
    /// </remarks>
    Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a transaction that is retried
    /// as a whole on a transient failure (§27, §30).
    /// </summary>
    /// <remarks>
    /// The delegate must load everything it needs itself: the change tracker
    /// is cleared before each attempt so a partially-applied retry cannot
    /// resurrect entities added by the attempt that failed.
    /// </remarks>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves the next value in a numbering sequence. The
    /// implementation must be safe under concurrency (§5).
    /// </summary>
    Task<long> NextSequenceValueAsync(string prefix, int year, CancellationToken cancellationToken = default);
}
