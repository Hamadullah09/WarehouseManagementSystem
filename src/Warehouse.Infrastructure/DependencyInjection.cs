using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Alarms;
using Warehouse.Application.Audit;
using Warehouse.Application.Documents;
using Warehouse.Application.Epcs;
using Warehouse.Application.Gates;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Validation;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, identity and the application services.</summary>
    public static IServiceCollection AddWarehouseInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException(
                "Connection string 'Warehouse' is not configured. Set ConnectionStrings:Warehouse "
                + "in configuration, an environment variable or a user secret.");

        services.AddDbContext<WarehouseDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(WarehouseDbContext).Assembly.FullName);

                // Transient network faults are expected on a warehouse LAN;
                // retry rather than failing a gate transaction outright.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);

                sql.CommandTimeout(30);
            }));

        services.AddScoped<IWarehouseDbContext>(sp => sp.GetRequiredService<WarehouseDbContext>());

        return services.AddWarehouseServices();
    }

    /// <summary>
    /// Registers everything above persistence. Split out so tests can supply
    /// their own <see cref="IWarehouseDbContext"/> registration.
    /// </summary>
    public static IServiceCollection AddWarehouseServices(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IValidationEngine, ValidationEngine>();

        services.AddScoped<INumberGenerator, NumberGenerator>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAlarmService, AlarmService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IEpcImportService, EpcImportService>();
        services.AddScoped<IDeviceSessionService, DeviceSessionService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();

        return services;
    }
}
