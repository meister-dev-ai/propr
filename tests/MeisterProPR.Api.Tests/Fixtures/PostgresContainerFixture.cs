using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace MeisterProPR.Api.Tests.Fixtures;

/// <summary>
///     Starts a single PostgreSQL container once for the entire "PostgresApiIntegration" collection.
///     Shared by <see cref="PrCrawlRestartTests" /> and <see cref="StartupRecoveryTests" /> so only
///     one container lifecycle is needed for the two tests.
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
///     Collection definition that wires <see cref="PostgresContainerFixture" /> for all tests
///     marked with <c>[Collection("PostgresApiIntegration")]</c>.
/// </summary>
[CollectionDefinition("PostgresApiIntegration")]
public sealed class PostgresApiIntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    // Marker class — no members needed.
}