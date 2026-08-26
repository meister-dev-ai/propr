// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.ProPR.Api.Tests.Fixtures;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Api.Tests.Features.Licensing;

[Collection("PostgresApiIntegration")]
public sealed class LicensingIdentityCommandTests(PostgresContainerFixture fixture)
{
    [SkippableFact]
    public async Task PrintLicensingIdentity_MigratedDatabase_PrintsThePersistedIdentity()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_identity_command_{Guid.NewGuid():N}";
        var databaseConnectionString = ConnectionStringFor(databaseName);
        var dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"propr-identity-command-{Guid.NewGuid():N}");

        await CreateDatabaseAsync(fixture.ConnectionString, databaseName);

        try
        {
            // The command does not migrate, so the schema has to be there before it runs. This is the state
            // the command is documented for: a database the API has already started against.
            await MigrateAsync(databaseConnectionString);

            using var process = StartIdentityCommand(databaseConnectionString, dataProtectionKeysPath);
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Empty((await standardError).Trim());
            var identity = Assert.Single(
                (await standardOutput).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.True(Guid.TryParse(identity, out _));

            await using var connection = new NpgsqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            // The identity is a singleton, so the row count is asserted alongside the value: reading a scalar
            // would report the first of any number of rows.
            await using var command = new NpgsqlCommand("SELECT identifier FROM licensing_identity;", connection);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync(), "The command persisted no licensing identity.");
            Assert.Equal(identity, reader.GetValue(0).ToString());
            Assert.False(await reader.ReadAsync(), "The command persisted more than one licensing identity.");
        }
        finally
        {
            await DropDatabaseAsync(fixture.ConnectionString, databaseName);

            if (Directory.Exists(dataProtectionKeysPath))
            {
                Directory.Delete(dataProtectionKeysPath, recursive: true);
            }
        }
    }

    /// <summary>
    ///     A database with no schema is reported rather than migrated. The command reads an identifier, and a
    ///     newer image run against a live installation would otherwise migrate it under the serving replicas.
    /// </summary>
    [SkippableFact]
    public async Task PrintLicensingIdentity_DatabaseWithoutTheSchema_ReportsItAndCreatesNothing()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_identity_command_{Guid.NewGuid():N}";
        var databaseConnectionString = ConnectionStringFor(databaseName);
        var dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"propr-identity-command-{Guid.NewGuid():N}");

        await CreateDatabaseAsync(fixture.ConnectionString, databaseName);

        try
        {
            using var process = StartIdentityCommand(databaseConnectionString, dataProtectionKeysPath);
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            Assert.Equal(1, process.ExitCode);
            Assert.Empty((await standardOutput).Trim());
            // The message names the remedy, because a shell caller cannot act on a relation name.
            Assert.Contains("licensing schema is not present", await standardError, StringComparison.OrdinalIgnoreCase);

            await using var connection = new NpgsqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';",
                connection);

            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropDatabaseAsync(fixture.ConnectionString, databaseName);

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

    private static async Task MigrateAsync(string databaseConnectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(databaseConnectionString, o => o.UseVector())
            .Options;

        await using var db = new MeisterProPRDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static Process StartIdentityCommand(string databaseConnectionString, string dataProtectionKeysPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MeisterDev.ProPR.Api.dll"));
        startInfo.ArgumentList.Add("--print-licensing-identity");
        startInfo.Environment["DB_CONNECTION_STRING"] = databaseConnectionString;
        startInfo.Environment["MEISTER_JWT_SECRET"] = "test-jwt-secret-at-least-32-chars-ok!!";
        startInfo.Environment["MEISTER_BOOTSTRAP_ADMIN_USER"] = "testadmin";
        startInfo.Environment["MEISTER_BOOTSTRAP_ADMIN_PASSWORD"] = "TestAdminPass1!";
        startInfo.Environment["MEISTER_DATA_PROTECTION_KEYS_PATH"] = dataProtectionKeysPath;

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The licensing identity command did not start.");
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
            Skip.If(true, "Skipping the licensing identity command test because the configured role may not CREATE DATABASE.");
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
