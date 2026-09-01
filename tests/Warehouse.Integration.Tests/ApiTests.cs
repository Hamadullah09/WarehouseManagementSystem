using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Persistence;
using Xunit;

namespace Warehouse.Integration.Tests;

/// <summary>
/// Boots the real host and exercises the composition root.
/// </summary>
/// <remarks>
/// The point of these is not business logic — that is covered exhaustively in
/// the domain and application suites — but the wiring around it: authentication,
/// authorisation, the problem-details shape, and the guards that stop
/// simulation endpoints doing anything they should not.
///
/// SQL Server is swapped for the in-memory provider so the suite runs anywhere,
/// including CI without a database. The full SQL Server path is covered by
/// <c>scripts/e2e.py</c> against a running instance.
/// </remarks>
public sealed class WarehouseApiFactory : WebApplicationFactory<Program>
{
    private readonly string _database = $"api-tests-{Guid.NewGuid():N}";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // AddDbContext registers more than the options object. Leaving any
            // of it behind makes EF see two providers in one container and
            // refuse to build, so everything the SQL Server registration
            // contributed has to go before the in-memory one is added.
            services.RemoveAll<DbContextOptions<WarehouseDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<WarehouseDbContext>();

            foreach (var descriptor in services
                         .Where(d => d.ServiceType.FullName?.StartsWith(
                             "Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
                         .ToList())
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<WarehouseDbContext>(options => options
                .UseInMemoryDatabase(_database)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            services.AddScoped<IWarehouseDbContext>(sp => sp.GetRequiredService<WarehouseDbContext>());
        });

        // The host's own startup path creates the schema and seeds it, so this
        // exercises the real bootstrap rather than a test-only substitute.
        return base.CreateHost(builder);
    }
}

public class ApiTests(WarehouseApiFactory factory) : IClassFixture<WarehouseApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var response = await Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Theory]
    [InlineData("/api/documents")]
    [InlineData("/api/gates")]
    [InlineData("/api/rfid/readers")]
    [InlineData("/api/alarms")]
    [InlineData("/api/dashboard")]
    [InlineData("/api/epcs")]
    public async Task Business_endpoints_require_authentication(string path)
    {
        var response = await Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} must not be anonymous");
    }

    [Fact]
    public async Task Bad_credentials_are_rejected_without_saying_why()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "nobody", password = "wrong-password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        // Uniform message: the endpoint must not reveal whether the account exists.
        problem!["detail"].ToString().Should().Be("The user name or password is incorrect.");
    }

    [Fact]
    public async Task Seeded_administrator_can_sign_in_and_must_change_its_password()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "admin", password = "ChangeMe.Development.1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var session = await response.Content.ReadFromJsonAsync<LoginResult>();

        session!.Token.Should().NotBeNullOrWhiteSpace();
        session.Roles.Should().Contain("Administrator");
        session.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_document_with_unregistered_epcs_returns_the_offending_values()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/documents/inward", new { epcs = new[] { "AABBCCDD", "EEFF0011" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ProblemWithOffending>();

        problem!.Title.Should().Be("Validation failed");
        problem.Offending.Should().BeEquivalentTo("AABBCCDD", "EEFF0011");
    }

    [Fact]
    public async Task Simulation_refuses_a_reader_that_is_not_a_simulator()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync("/api/simulation/readers/NOT-A-SIMULATOR/gpio-on", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        problem!["title"].ToString().Should().Be("No simulated reader");
    }

    [Fact]
    public async Task Enums_are_serialised_as_names_not_numbers()
    {
        var client = await AuthenticatedClientAsync();

        // The SPA and the SignalR hub both switch on these strings; numbers
        // would silently break every state comparison in the display.
        var body = await client.GetStringAsync("/api/gates");

        body.Should().NotBeNullOrWhiteSpace();
        body.Should().MatchRegex("\"state\"\\s*:\\s*\"[A-Za-z]+\"");
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "admin", password = "ChangeMe.Development.1" });

        var session = await response.Content.ReadFromJsonAsync<LoginResult>();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.Token);

        return client;
    }

    private sealed record LoginResult(
        string Token,
        DateTimeOffset ExpiresAt,
        string UserName,
        string DisplayName,
        string[] Roles,
        bool MustChangePassword);

    private sealed record ProblemWithOffending(string Title, string Detail, int Status, string[] Offending);
}
