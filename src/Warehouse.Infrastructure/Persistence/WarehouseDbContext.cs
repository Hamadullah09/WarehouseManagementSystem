using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Warehouse.Application.Abstractions;
using Warehouse.Domain;
using Warehouse.Domain.Entities;

namespace Warehouse.Infrastructure.Persistence;

public class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options)
    : DbContext(options), IWarehouseDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<EpcTag> EpcTags => Set<EpcTag>();

    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();

    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    public DbSet<Gate> Gates => Set<Gate>();

    public DbSet<Reader> Readers => Set<Reader>();

    public DbSet<ReaderEvent> ReaderEvents => Set<ReaderEvent>();

    public DbSet<GpioEvent> GpioEvents => Set<GpioEvent>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentItem> DocumentItems => Set<DocumentItem>();

    public DbSet<GateCycle> GateCycles => Set<GateCycle>();

    public DbSet<GateCycleEpc> GateCycleEpcs => Set<GateCycleEpc>();

    public DbSet<Alarm> Alarms => Set<Alarm>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();

    public async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        // The in-memory provider used by unit tests has no transaction support.
        // Returning null lets callers run the same code path without branching
        // on the provider, while real deployments always get a transaction.
        if (!Database.IsRelational())
        {
            return null;
        }

        return Database.CurrentTransaction
            ?? await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an operation as a retriable transactional unit.
    /// </summary>
    /// <remarks>
    /// SQL Server is configured with <c>EnableRetryOnFailure</c> so a dropped
    /// packet on a warehouse LAN does not fail a gate transaction. That
    /// strategy refuses a user-initiated transaction unless the transaction is
    /// itself inside the retriable unit, which is what this does.
    ///
    /// The change tracker is cleared before each attempt. Without that, a
    /// retry would re-run the delegate against a context still holding the
    /// entities the failed attempt added, and commit them twice.
    /// </remarks>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!Database.IsRelational())
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        // Already inside someone else's transaction: join it rather than nest.
        if (Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        var strategy = Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            ChangeTracker.Clear();

            await using var transaction = await Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var result = await operation(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reserves the next value in a numbering sequence (§5).
    /// </summary>
    /// <remarks>
    /// On SQL Server this is a single statement taking an update lock on the
    /// counter row, so concurrent document creation serialises on the row
    /// rather than racing to a unique-index violation. The row is created on
    /// first use; the insert races are resolved by retrying the update, which
    /// is why the unique index on (Prefix, Year) is load-bearing rather than
    /// merely tidy.
    ///
    /// Other providers fall back to tracked-entity increment, which is correct
    /// for tests but is not the production path.
    /// </remarks>
    public virtual async Task<long> NextSequenceValueAsync(
        string prefix,
        int year,
        CancellationToken cancellationToken = default)
    {
        if (Database.IsSqlServer())
        {
            return await NextSqlServerSequenceValueAsync(prefix, year, cancellationToken).ConfigureAwait(false);
        }

        var sequence = await NumberSequences
            .FirstOrDefaultAsync(s => s.Prefix == prefix && s.Year == year, cancellationToken)
            .ConfigureAwait(false);

        if (sequence is null)
        {
            sequence = new NumberSequence { Prefix = prefix, Year = year, LastValue = 0 };
            NumberSequences.Add(sequence);
        }

        sequence.LastValue++;

        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return sequence.LastValue;
    }

    private async Task<long> NextSqlServerSequenceValueAsync(
        string prefix,
        int year,
        CancellationToken cancellationToken)
    {
        const string incrementSql = """
            DECLARE @next BIGINT;
            UPDATE [NumberSequences] WITH (UPDLOCK, ROWLOCK)
               SET @next = [LastValue] = [LastValue] + 1
             WHERE [Prefix] = {0} AND [Year] = {1};
            SELECT ISNULL(@next, -1);
            """;

        const string seedSql = """
            IF NOT EXISTS (SELECT 1 FROM [NumberSequences] WHERE [Prefix] = {0} AND [Year] = {1})
                INSERT INTO [NumberSequences] ([Prefix], [Year], [LastValue]) VALUES ({0}, {1}, 0);
            """;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // ToListAsync rather than SingleAsync: the statement above is a
            // multi-statement batch, and any LINQ operator would make EF try
            // to compose a subquery over non-composable SQL.
            var rows = await Database
                .SqlQueryRaw<long>(incrementSql, prefix, year)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var value = rows.Count > 0 ? rows[0] : -1;

            if (value >= 0)
            {
                return value;
            }

            try
            {
                await Database.ExecuteSqlRawAsync(seedSql, [prefix, year], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another writer seeded it first; the next iteration will find it.
            }
        }

        throw new InvalidOperationException(
            $"Unable to allocate a number from sequence {prefix}-{year} after 3 attempts.");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        ConfigureIdentity(b);
        ConfigureCatalog(b);
        ConfigureHardware(b);
        ConfigureDocuments(b);
        ConfigureCycles(b);
        ConfigureOperations(b);
    }

    private static void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<Role>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.UserName).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.UserName).IsUnique();
        });

        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });

            e.HasOne(x => x.User).WithMany(u => u.UserRoles)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Role).WithMany(r => r.UserRoles)
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureCatalog(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.Uom).HasMaxLength(16);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<EpcTag>(e =>
        {
            e.Property(x => x.Epc).HasMaxLength(Epc.MaxLength).IsRequired();
            e.Property(x => x.ItemCode).HasMaxLength(64);
            e.Property(x => x.ItemName).HasMaxLength(256);
            e.Property(x => x.SerialNumber).HasMaxLength(128);
            e.Property(x => x.CartonNumber).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.RowVersion).IsRowVersion();

            // The single most important index in the schema: every gate read
            // resolves through it.
            e.HasIndex(x => x.Epc).IsUnique();

            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.ProductId, x.Status });

            e.HasOne(x => x.Product).WithMany(p => p.Tags)
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<InventoryItem>(e =>
        {
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.ProductId).IsUnique();

            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InventoryMovement>(e =>
        {
            e.HasIndex(x => x.GateCycleId);
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => new { x.EpcTagId, x.OccurredAt });

            e.HasOne(x => x.EpcTag).WithMany()
                .HasForeignKey(x => x.EpcTagId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureHardware(ModelBuilder b)
    {
        b.Entity<Gate>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Location).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.Code).IsUnique();

            // No cascade: clearing a gate must never delete a document.
            e.HasOne(x => x.ActiveDocument).WithMany()
                .HasForeignKey(x => x.ActiveDocumentId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<Reader>(e =>
        {
            e.Property(x => x.ReaderId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(64);
            e.Property(x => x.FirmwareVersion).HasMaxLength(64);
            e.Property(x => x.HardwareVersion).HasMaxLength(64);
            e.Property(x => x.EnabledAntennas).HasMaxLength(64);
            e.Property(x => x.GpioState).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(1024);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.ReaderId).IsUnique();

            e.HasOne(x => x.Gate).WithMany(g => g.Readers)
                .HasForeignKey(x => x.GateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReaderEvent>(e =>
        {
            e.Property(x => x.Message).HasMaxLength(2048);
            e.Property(x => x.SdkOperation).HasMaxLength(128);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.HasIndex(x => new { x.ReaderId, x.OccurredAt });

            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GpioEvent>(e =>
        {
            e.Property(x => x.Pin).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.GateId, x.OccurredAt });
            e.HasIndex(x => x.GateCycleId);

            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureDocuments(ModelBuilder b)
    {
        b.Entity<Document>(e =>
        {
            e.Property(x => x.DocumentNumber).HasMaxLength(32).IsRequired();
            e.Property(x => x.UserDisplayName).HasMaxLength(128);
            e.Property(x => x.Reference).HasMaxLength(128);
            e.Property(x => x.Notes).HasMaxLength(2048);
            e.Property(x => x.CancelledReason).HasMaxLength(512);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.Ignore(x => x.BalanceArticles);
            e.Ignore(x => x.BalanceQuantity);

            e.HasIndex(x => x.DocumentNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.GateId, x.Status });
            e.HasIndex(x => new { x.Type, x.CreatedAt });

            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Gate).WithMany()
                .HasForeignKey(x => x.GateId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<DocumentItem>(e =>
        {
            e.Property(x => x.Epc).HasMaxLength(Epc.MaxLength).IsRequired();

            // One EPC may appear on a document only once.
            e.HasIndex(x => new { x.DocumentId, x.Epc }).IsUnique();
            e.HasIndex(x => x.Epc);

            e.HasOne(x => x.Document).WithMany(d => d.Items)
                .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.EpcTag).WithMany()
                .HasForeignKey(x => x.EpcTagId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCycles(ModelBuilder b)
    {
        b.Entity<GateCycle>(e =>
        {
            e.Property(x => x.CycleId).HasMaxLength(32).IsRequired();
            e.Property(x => x.TriggerKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.ValidationSummary).HasMaxLength(512);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasIndex(x => x.CycleId).IsUnique();

            // The idempotency guarantee: one physical gate event, one cycle.
            e.HasIndex(x => x.TriggerKey).IsUnique();

            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => new { x.GateId, x.StartedAt });
            e.HasIndex(x => x.Status);

            e.HasOne(x => x.Gate).WithMany(g => g.Cycles)
                .HasForeignKey(x => x.GateId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Reader).WithMany()
                .HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Document).WithMany(d => d.Cycles)
                .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GateCycleEpc>(e =>
        {
            e.Property(x => x.Epc).HasMaxLength(Epc.MaxLength).IsRequired();

            e.HasIndex(x => x.GateCycleId);
            e.HasIndex(x => x.Epc);
            e.HasIndex(x => new { x.GateCycleId, x.Epc }).IsUnique();
            e.HasIndex(x => x.Classification);

            e.HasOne(x => x.GateCycle).WithMany(c => c.Epcs)
                .HasForeignKey(x => x.GateCycleId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureOperations(ModelBuilder b)
    {
        b.Entity<Alarm>(e =>
        {
            e.Property(x => x.AlarmId).HasMaxLength(32).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Epc).HasMaxLength(Epc.MaxLength);
            e.Property(x => x.ResolutionNotes).HasMaxLength(2048);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasIndex(x => x.AlarmId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Status, x.RaisedAt });
            e.HasIndex(x => x.GateCycleId);
            e.HasIndex(x => x.AlarmType);

            e.HasOne(x => x.Gate).WithMany()
                .HasForeignKey(x => x.GateId).OnDelete(DeleteBehavior.NoAction);

            e.HasOne(x => x.Document).WithMany()
                .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.NoAction);

            e.HasOne(x => x.GateCycle).WithMany(c => c.Alarms)
                .HasForeignKey(x => x.GateCycleId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.ResolvedByUser).WithMany()
                .HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.DocumentNumber).HasMaxLength(32);
            e.Property(x => x.CycleId).HasMaxLength(32);
            e.Property(x => x.Epc).HasMaxLength(Epc.MaxLength);
            e.Property(x => x.PreviousState).HasMaxLength(64);
            e.Property(x => x.NewState).HasMaxLength(64);
            e.Property(x => x.Result).HasMaxLength(512);
            e.Property(x => x.Details).HasMaxLength(4000);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);

            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.Action, x.OccurredAt });
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.GateCycleId);
            e.HasIndex(x => x.Epc);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Value).HasMaxLength(2048);
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.Category).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<NumberSequence>(e =>
        {
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.Prefix, x.Year }).IsUnique();
        });
    }
}
