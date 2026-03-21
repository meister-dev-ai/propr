using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace MeisterProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Starts a single PostgreSQL container once for the entire "PostgresIntegration" collection.
///     Avoids the instability of spinning up one container per test method with Podman.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => this._postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await this._postgres.StartAsync();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(this.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var ctx = new MeisterProPRDbContext(options);
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await this._postgres.DisposeAsync();
    }
}

/// <summary>
///     xUnit collection definition that wires <see cref="PostgresContainerFixture" /> as a shared
///     fixture for all tests marked with <c>[Collection("PostgresIntegration")]</c>.
/// </summary>
[CollectionDefinition("PostgresIntegration")]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class — no members needed.
}
