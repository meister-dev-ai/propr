// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MeisterProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Starts a single PostgreSQL container once for the entire "PostgresIntegration" collection.
///     Avoids the instability of spinning up one container per test method with Podman.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private string? _connectionString;
    private PostgreSqlContainer? _postgres;
    private string? _skipReason;

    private bool _startedContainer;

    public bool IsAvailable => this._skipReason is null;

    public string ConnectionString => this._connectionString
                                      ?? throw new InvalidOperationException("Postgres container fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        var externalConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")?.Trim();
        if (!string.IsNullOrWhiteSpace(externalConnectionString))
        {
            this._connectionString = externalConnectionString;
            if (await this.TryMigrateAsync())
            {
                return;
            }

            this._connectionString = null;
        }

        if (string.IsNullOrWhiteSpace(this._connectionString))
        {
            try
            {
                this._postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                    .Build();

                await this._postgres.StartAsync();
                this._startedContainer = true;
                this._connectionString = this._postgres.GetConnectionString();
            }
            catch (DockerUnavailableException ex)
            {
                this._skipReason =
                    "Skipping PostgresIntegration tests because Docker is unavailable. " +
                    "Start Docker Desktop or Podman, or run the DB-backed tests with DB_CONNECTION_STRING pointing at a PostgreSQL instance. " +
                    $"Original error: {ex.Message}";
                return;
            }
            catch (ResourceReaperException ex)
            {
                this._skipReason =
                    "Skipping PostgresIntegration tests because Testcontainers could not start its resource reaper. " +
                    "Start Docker Desktop or Podman, or run the DB-backed tests with DB_CONNECTION_STRING pointing at a PostgreSQL instance. " +
                    $"Original error: {ex.Message}";
                return;
            }
        }

        await this.TryMigrateAsync(true);
    }

    public async Task DisposeAsync()
    {
        if (!this._startedContainer || this._postgres is null)
        {
            return;
        }

        await this._postgres.DisposeAsync();
    }

    public void SkipIfUnavailable()
    {
        Skip.If(this._skipReason is not null, this._skipReason);
    }

    private async Task<bool> TryMigrateAsync(bool throwOnFailure = false)
    {
        if (string.IsNullOrWhiteSpace(this._connectionString))
        {
            return false;
        }

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(this.ConnectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        try
        {
            await using var ctx = new MeisterProPRDbContext(options);
            await ctx.Database.MigrateAsync();
            return true;
        }
        catch (NpgsqlException) when (!throwOnFailure)
        {
            return false;
        }
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
