using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Warehouse.Api.Realtime;
using Warehouse.Api.Services;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Documents;
using Warehouse.Application.Gates;
using Warehouse.Application.Options;
using Warehouse.Application.Realtime;
using Warehouse.Infrastructure;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Rfid.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging (§38)

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName());

// ------------------------------------------------------------ options (§34, §44)

builder.Services.AddOptions<RfidOptions>()
    .Bind(builder.Configuration.GetSection(RfidOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<GateOptions>()
    .Bind(builder.Configuration.GetSection(GateOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<DocumentOptions>()
    .Bind(builder.Configuration.GetSection(DocumentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AlarmOptions>()
    .Bind(builder.Configuration.GetSection(AlarmOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ------------------------------------------------------------------ services

builder.Services.AddWarehouseInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<TokenService>();

// Singletons: the registry owns reader instances and the gate service owns
// per-gate memory, so both must outlive any request.
builder.Services.AddSingleton<RfidReaderRegistry>();
builder.Services.AddSingleton<IRfidReaderRegistry>(sp => sp.GetRequiredService<RfidReaderRegistry>());
builder.Services.AddSingleton<IGateNotifier, SignalRGateNotifier>();
builder.Services.AddSingleton<IGateIndicator, GateIndicator>();
builder.Services.AddSingleton<IGateCycleService, GateCycleService>();
builder.Services.AddHostedService<RfidHostedService>();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
})
.AddJsonProtocol(options =>
{
    // SignalR has its own serializer, separate from MVC's. Without this the
    // hub would send enums as integers while the REST endpoints send names,
    // and a display that hydrates from REST then updates over the hub would
    // silently stop recognising its own gate states.
    options.PayloadSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums travel as names so the SPA and the logs read the same way.
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<WarehouseDbContext>("database");

// ------------------------------------------------------------- security (§33)

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The Jwt configuration section is missing.");

if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be at least 32 characters. Supply it through an environment variable "
        + "(Jwt__SigningKey), a user secret or a key vault -- never a file in source control.");
}

// The development key ships in appsettings.Development.json and is therefore
// public. Length alone would not catch it, so refuse it by name outside
// Development: a deployment running on a published key can have its tokens
// forged by anyone who has read the repository.
if (!builder.Environment.IsDevelopment()
    && jwt.SigningKey.Contains("development", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Jwt:SigningKey is still the development key, which is published in source control. "
        + "Set a real secret in Jwt__SigningKey before running outside Development.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // Browsers cannot set headers on a WebSocket handshake, so SignalR
        // passes the token in the query string. Accept it only for hub paths.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(token)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Tight bucket on the one anonymous endpoint that accepts secrets.
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Generous global ceiling: enough to stop a runaway client, never enough
    // to throttle a busy gate.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1200,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddPolicy("spa", policy =>
{
    if (corsOrigins.Length > 0)
    {
        policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // required for the SignalR handshake
    }
}));

var app = builder.Build();

// ----------------------------------------------------------------- pipeline

app.UseSerilogRequestLogging();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var error = feature?.Error;

    // Business rule violations are the caller's problem, not a server fault,
    // and carry the offending values so the UI can point at them.
    var (status, title, detail, offending) = error switch
    {
        WarehouseValidationException v =>
            (StatusCodes.Status400BadRequest, "Validation failed", v.Message, v.Offending),

        UnauthorizedAccessException u =>
            (StatusCodes.Status403Forbidden, "Not permitted", u.Message, (IReadOnlyList<string>)[]),

        KeyNotFoundException k =>
            (StatusCodes.Status404NotFound, "Not found", k.Message, (IReadOnlyList<string>)[]),

        InvalidOperationException i =>
            (StatusCodes.Status409Conflict, "Operation not allowed", i.Message, (IReadOnlyList<string>)[]),

        DbUpdateConcurrencyException =>
            (StatusCodes.Status409Conflict, "Concurrent modification",
                "Another user changed this record. Reload and try again.", (IReadOnlyList<string>)[]),

        _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
            "An unexpected error occurred. The incident has been logged.", (IReadOnlyList<string>)[])
    };

    if (status == StatusCodes.Status500InternalServerError)
    {
        app.Logger.LogError(error, "Unhandled exception on {Path}", context.Request.Path);
    }

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";

    var problem = new ProblemDetails
    {
        Title = title,
        Detail = detail,
        Status = status,
        Instance = context.Request.Path
    };

    if (offending.Count > 0)
    {
        problem.Extensions["offending"] = offending;
    }

    await context.Response.WriteAsJsonAsync(problem);
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors("spa");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<GateHub>("/hubs/gate");
app.MapHealthChecks("/health");

// The SPA is served as static files from wwwroot in production; unmatched
// routes fall through to index.html so client-side routing works on reload.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

// ------------------------------------------------------- database bootstrap

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();

    // Migrations are relational-only. The in-memory provider used by the API
    // test host has no schema to migrate, so it is created directly instead.
    if (db.Database.IsRelational())
    {
        app.Logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>();
    await seeder.SeedAsync();
}

app.Run();

/// <summary>Exposed so the integration test host can reference this assembly.</summary>
public partial class Program;
