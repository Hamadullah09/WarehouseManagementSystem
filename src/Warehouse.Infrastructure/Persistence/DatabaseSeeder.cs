using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Domain.Entities;
using Warehouse.Rfid.Abstractions;

namespace Warehouse.Infrastructure.Persistence;

public interface IDatabaseSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Brings the database to a usable baseline: roles, a bootstrap administrator,
/// and the gates and readers named in configuration.
/// </summary>
/// <remarks>
/// No business data is seeded. EPCs, products and documents come from the
/// import pipeline or the API, never from source (§44).
///
/// The bootstrap administrator password is read from configuration. If none is
/// supplied a strong random one is generated and written to the log exactly
/// once, flagged for mandatory change at first login. A password is never
/// hard-coded (§33).
/// </remarks>
public sealed class DatabaseSeeder(
    WarehouseDbContext db,
    IClock clock,
    IConfiguration configuration,
    IOptions<RfidOptions> rfidOptions,
    ILogger<DatabaseSeeder> logger) : IDatabaseSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync(cancellationToken).ConfigureAwait(false);
        await SeedAdministratorAsync(cancellationToken).ConfigureAwait(false);
        await SeedGatesAndReadersAsync(cancellationToken).ConfigureAwait(false);
        await SeedSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        var descriptions = new Dictionary<string, string>
        {
            [RoleNames.Administrator] = "Full access, including user and reader administration.",
            [RoleNames.Supervisor] = "Creates and cancels documents, resolves alarms.",
            [RoleNames.Operator] = "Runs gates and views documents."
        };

        foreach (var name in RoleNames.All.Except(existing, StringComparer.Ordinal))
        {
            db.Roles.Add(new Role { Name = name, Description = descriptions[name] });
            logger.LogInformation("Seeded role {Role}", name);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedAdministratorAsync(CancellationToken cancellationToken)
    {
        var userName = configuration["Seed:AdminUserName"] ?? "admin";

        if (await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var configured = configuration["Seed:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(configured);
        var password = generated ? GeneratePassword() : configured!;

        var user = new User
        {
            UserName = userName,
            DisplayName = configuration["Seed:AdminDisplayName"] ?? "System Administrator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = clock.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var adminRole = await db.Roles.FirstAsync(r => r.Name == RoleNames.Administrator, cancellationToken)
            .ConfigureAwait(false);

        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (generated)
        {
            // Printed once, on first run only. Change it and it is never shown again.
            logger.LogWarning(
                "Created bootstrap administrator {UserName} with generated password: {Password} "
                + "-- change it at first login; it will not be shown again.",
                userName, password);
        }
        else
        {
            logger.LogInformation(
                "Created bootstrap administrator {UserName} from configuration; password change is required at first login.",
                userName);
        }
    }

    private async Task SeedGatesAndReadersAsync(CancellationToken cancellationToken)
    {
        foreach (var cfg in rfidOptions.Value.Readers.Where(r => r.Enabled))
        {
            var gate = await db.Gates.FirstOrDefaultAsync(g => g.Code == cfg.GateId, cancellationToken)
                .ConfigureAwait(false);

            if (gate is null)
            {
                gate = new Gate
                {
                    Code = cfg.GateId,
                    Name = cfg.GateId,
                    IsActive = true,
                    CreatedAt = clock.UtcNow
                };

                db.Gates.Add(gate);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                logger.LogInformation("Seeded gate {GateCode}", gate.Code);
            }

            var reader = await db.Readers.FirstOrDefaultAsync(r => r.ReaderId == cfg.ReaderId, cancellationToken)
                .ConfigureAwait(false);

            if (reader is null)
            {
                db.Readers.Add(new Reader
                {
                    ReaderId = cfg.ReaderId,
                    Name = cfg.Name,
                    GateId = gate.Id,
                    IpAddress = cfg.SerialPort ?? cfg.ReaderHost,
                    Port = cfg.SerialPort is null ? cfg.ReaderPort : null,
                    Model = cfg.Driver == RfidDriverKind.U300 ? "Chainway U300" : "Simulator",
                    EnabledAntennas = string.Join(',', cfg.Antennas),
                    IsActive = true
                });

                logger.LogInformation("Seeded reader {ReaderId} on gate {GateCode}", cfg.ReaderId, gate.Code);
            }
            else
            {
                // Configuration is the source of truth for wiring; the row follows it.
                reader.Name = cfg.Name;
                reader.GateId = gate.Id;
                reader.IpAddress = cfg.SerialPort ?? cfg.ReaderHost;
                reader.Port = cfg.SerialPort is null ? cfg.ReaderPort : null;
                reader.EnabledAntennas = string.Join(',', cfg.Antennas);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SeedSettingsAsync(CancellationToken cancellationToken)
    {
        var defaults = new (string Key, string Value, string Category, string Description)[]
        {
            ("Display.RefreshSeconds", "5", "Display", "Fallback poll interval when the real-time channel is down."),
            ("Display.ShowRssi", "false", "Display", "Show signal strength beside each EPC on the gate display."),
            ("Gate.ShowMissingEpcList", "true", "Gate", "List the missing EPCs on the gate display after a failed cycle.")
        };

        var existing = await db.SystemSettings.Select(s => s.Key).ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var (key, value, category, description) in defaults)
        {
            if (existing.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                Category = category,
                Description = description,
                UpdatedAt = clock.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";

        return string.Create(24, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
