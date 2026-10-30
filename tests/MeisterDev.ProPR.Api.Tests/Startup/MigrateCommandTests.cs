// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.ProPR.Api.Tests.Fixtures;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Api.Tests.Startup;

[Collection("PostgresApiIntegration")]
public sealed class MigrateCommandTests(PostgresContainerFixture fixture)
{
    // The migrations of an empty database end well within this time.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);

    [SkippableFact]
    public async Task Migrate_EmptyDatabase_AppliesEveryMigrationAndEnds()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_migrate_command_{Guid.NewGuid():N}";
        var databaseConnectionString = this.ConnectionStringFor(databaseName);
        var dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"propr-migrate-command-{Guid.NewGuid():N}");

        await CreateDatabaseAsync(fixture.ConnectionString, databaseName);

        try
        {
            var (exitCode, output) = await RunMigrateCommandAsync(databaseConnectionString, dataProtectionKeysPath);

            Assert.True(exitCode == 0, output);

            var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(databaseConnectionString, o => o.UseVector())
                .Options;
            await using var db = new MeisterProPRDbContext(options);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            try
            {
                await DropDatabaseAsync(fixture.ConnectionString, databaseName);
            }
            finally
            {
                if (Directory.Exists(dataProtectionKeysPath))
                {
                    Directory.Delete(dataProtectionKeysPath, recursive: true);
                }
            }
        }
    }

    /// <summary>
    ///     A deploy reads a failed migration from the exit code alone, so a migration that cannot run ends with an
    ///     error code, here because the database refuses the credentials.
    /// </summary>
    [SkippableFact]
    public async Task Migrate_MigrationFails_EndsWithAnError()
    {
        fixture.SkipIfUnavailable();

        var refusedConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Password = $"wrong-{Guid.NewGuid():N}",
            Timeout = 10,
        }.ConnectionString;
        var dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"propr-migrate-command-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await RunMigrateCommandAsync(refusedConnectionString, dataProtectionKeysPath);

            Assert.True(exitCode != 0, output);
        }
        finally
        {
            if (Directory.Exists(dataProtectionKeysPath))
            {
                Directory.Delete(dataProtectionKeysPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Migrate_WithoutConnectionString_EndsWithAnError()
    {
        var dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"propr-migrate-command-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await RunMigrateCommandAsync(null, dataProtectionKeysPath);

            Assert.Equal(1, exitCode);
            Assert.Contains("--migrate needs the connection string", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataProtectionKeysPath))
            {
                Directory.Delete(dataProtectionKeysPath, recursive: true);
            }
        }
    }

    private string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

    private static async Task<(int ExitCode, string Output)> RunMigrateCommandAsync(string? databaseConnectionString, string dataProtectionKeysPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MeisterDev.ProPR.Api.dll"));
        startInfo.ArgumentList.Add("--migrate");
        startInfo.Environment.Remove("DB_CONNECTION_STRING");
        if (databaseConnectionString is not null)
        {
            startInfo.Environment["DB_CONNECTION_STRING"] = databaseConnectionString;
        }

        startInfo.Environment["MEISTER_JWT_SECRET"] = "test-jwt-secret-at-least-32-chars-ok!!";
        startInfo.Environment["MEISTER_DATA_PROTECTION_KEYS_PATH"] = dataProtectionKeysPath;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The migrate command did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The migrate command did not end within {CommandTimeout.TotalMinutes} minutes.");
        }

        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static async Task CreateDatabaseAsync(string connectionString, string databaseName)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            Skip.If(true, "Skipping the migrate command test because the configured role may not CREATE DATABASE.");
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }
}
