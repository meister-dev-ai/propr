// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     Starts one PostgreSQL container for each collection that binds this fixture, and shares it between the
///     tests of that collection. Avoids the instability of spinning up one container per test method with Podman.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private const string RootlessPodmanSocketPath = "/run/user/1000/podman/podman.sock";

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _migrationTemplates = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _templateDatabaseNames = [];

    private string? _connectionString;
    private string? _externalAdminConnectionString;
    private string? _externalDatabaseName;
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
            this._connectionString = await this.TryCreateExternalDatabaseAsync(externalConnectionString);
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
                var postgresBuilder = new PostgreSqlBuilder("pgvector/pgvector:pg17");

                // Rootless Podman commonly exposes its API socket here instead of /var/run/docker.sock.
                var localPodmanSocket = TryGetLocalPodmanSocket();
                if (localPodmanSocket is not null)
                {
                    postgresBuilder = postgresBuilder.WithDockerEndpoint(localPodmanSocket);
                }

                this._postgres = postgresBuilder
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
        if (this._externalDatabaseName is not null && this._externalAdminConnectionString is not null)
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var templateName in this._templateDatabaseNames)
            {
                await ExecuteAsync(
                    this._externalAdminConnectionString,
                    $"DROP DATABASE IF EXISTS \"{templateName}\" WITH (FORCE);");
            }

            await ExecuteAsync(
                this._externalAdminConnectionString,
                $"DROP DATABASE IF EXISTS \"{this._externalDatabaseName}\" WITH (FORCE);");
        }

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

    /// <summary>
    ///     Creates the database <paramref name="databaseName" /> with its schema at <paramref name="migration" />. The
    ///     caller drops the database when the test is done with it.
    /// </summary>
    /// <remarks>
    ///     Migrating an empty database to a recent migration applies more than a hundred migrations and takes about
    ///     two seconds, and several migration tests start from the same migration. The first request for a migration
    ///     therefore migrates a template database once, and every request copies that template, which takes about
    ///     fifty milliseconds. The copy has the same schema and migration history as a database migrated directly.
    /// </remarks>
    public async Task CreateDatabaseAtMigrationAsync(string databaseName, string migration)
    {
        var templateName = await this._migrationTemplates
            .GetOrAdd(migration, target => new Lazy<Task<string>>(() => this.CreateMigrationTemplateAsync(target)))
            .Value;

        await ExecuteAsync(this.ConnectionString, $"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{templateName}\";");
    }

    private async Task<string> CreateMigrationTemplateAsync(string migration)
    {
        var templateName = $"propr_template_{Guid.NewGuid():N}";
        await ExecuteAsync(this.ConnectionString, $"CREATE DATABASE \"{templateName}\";");
        this._templateDatabaseNames.Add(templateName);

        // PostgreSQL refuses to copy a database while a connection to it is open. Without pooling, the connection
        // that applied the migrations closes when the context is disposed.
        var connectionString = new NpgsqlConnectionStringBuilder(this.ConnectionString)
        {
            Database = templateName,
            Pooling = false,
        }.ConnectionString;

        await using (var context = CreateContext(connectionString))
        {
            await context.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migration);
        }

        return templateName;
    }

    private async Task<bool> TryMigrateAsync(bool throwOnFailure = false)
    {
        if (string.IsNullOrWhiteSpace(this._connectionString))
        {
            return false;
        }

        try
        {
            await using var ctx = CreateContext(this.ConnectionString);
            await ctx.Database.MigrateAsync();
            return true;
        }
        catch (NpgsqlException) when (!throwOnFailure)
        {
            return false;
        }
    }

    /// <summary>
    ///     Creates a database of its own for this fixture instance on the server named by
    ///     <c>DB_CONNECTION_STRING</c>, and returns its connection string.
    /// </summary>
    /// <remarks>
    ///     An assembly can bind several collections to this fixture, and xUnit runs those collections in parallel.
    ///     Some tests delete whole tables, so two collections on one database would interfere. The configured
    ///     database serves only as the connection for creating and dropping the per-fixture database. Returns
    ///     <see langword="null" /> when the server cannot be reached, so the caller falls back to a container.
    /// </remarks>
    private async Task<string?> TryCreateExternalDatabaseAsync(string externalConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(externalConnectionString);
        var databaseName = $"{builder.Database ?? "propr_test"}_{Guid.NewGuid():N}";
        if (databaseName.Length > 63)
        {
            databaseName = $"propr_test_{Guid.NewGuid():N}";
        }

        try
        {
            await ExecuteAsync(externalConnectionString, $"CREATE DATABASE \"{databaseName}\";");
        }
        catch (NpgsqlException)
        {
            return null;
        }

        this._externalAdminConnectionString = externalConnectionString;
        this._externalDatabaseName = databaseName;
        builder.Database = databaseName;
        return builder.ConnectionString;
    }

    private static MeisterProPRDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string? TryGetLocalPodmanSocket()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return null;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        return File.Exists(RootlessPodmanSocketPath)
            ? $"unix://{RootlessPodmanSocketPath}"
            : null;
    }
}

/// <summary>
///     xUnit collection definition that wires <see cref="PostgresContainerFixture" /> as a shared
///     fixture for all tests marked with <c>[Collection("PostgresIntegration")]</c>.
/// </summary>
