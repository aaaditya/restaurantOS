using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL database (created and migrated once per test run).
/// Connection: env var RESTAURANTOS_TEST_PG (admin connection string), defaulting to the local dev server.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _adminConnection;
    private readonly string _database = $"restaurantos_it_{Guid.NewGuid():N}";

    public ApiFactory()
    {
        _adminConnection = Environment.GetEnvironmentVariable("RESTAURANTOS_TEST_PG")
                           ?? "Host=localhost;Port=5432;Database=postgres;Username=restaurantos;Password=restaurantos";
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database }.ConnectionString;
    }

    public string ConnectionString { get; }

    public const string MockPaymentSecret = "test-mock-payment-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "100000");
        builder.UseSetting("RateLimiting:PublicPermitPerMinute", "100000");
        builder.UseSetting("Integrations:MockPaymentWebhookSecret", MockPaymentSecret);
        builder.UseSetting("Integrations:EnableMockSimulators", "true");
    }

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        _ = Server; // boots the host, which applies migrations
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Direct database access that bypasses tenant filters, for assertions and test setup.</summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        RestaurantOS.Infrastructure.DependencyInjection.Configure(options, ConnectionString);
        await using var db = new AppDbContext(options.Options, new FixedTenantContext(null));
        await action(db);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
