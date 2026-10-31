// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.ProCursor.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Data;

/// <summary>
///     Runs the migrations as the owner of the database, a role without superuser rights, on a server that refuses
///     every <c>CREATE EXTENSION</c>. Azure Database for PostgreSQL refuses the statement for vector to every role
///     outside <c>azure_pg_admin</c>, also with <c>IF NOT EXISTS</c> and when the extension exists, so an administrator
///     creates the extension beforehand and the migrations must not run the statement.
/// </summary>
[Collection("PostgresIntegration3")]
public sealed class ManagedPostgresExtensionMigrationTests(PostgresContainerFixture fixture)
{
    private const string OwnerPassword = "owner-password";

    [Fact]
    public async Task ReviewDatabaseMigratesWhenTheServerRefusesCreateExtensionAndVectorExists()
    {
        fixture.SkipIfUnavailable();
        await this.WithManagedDatabaseAsync(async connectionString =>
        {
            await using var db = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>()
                    .UseNpgsql(connectionString, options => options.UseVector()).Options);
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        });
    }

    // MigrateAsync of this context first builds its model snapshot, which fails for ProCursorIndexJob.Status under
    // EF Core 10, so the test runs the SQL script of the migrations as the owner.
    [Fact]
    public async Task ProCursorMigrationScriptRunsWhenTheServerRefusesCreateExtensionAndVectorExists()
    {
        fixture.SkipIfUnavailable();
        await this.WithManagedDatabaseAsync(async connectionString =>
        {
            await using var db = new ProCursorOperationalDbContext(
                new DbContextOptionsBuilder<ProCursorOperationalDbContext>()
                    .UseNpgsql(connectionString, options => options.UseVector()).Options);
            await ExecuteAsync(connectionString, db.GetService<IMigrator>().GenerateScript());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        });
    }

    // Creates a database that a role without superuser rights owns, creates vector in it as the administrator and
    // adds an event trigger that refuses every later CREATE EXTENSION. The action receives the connection string of
    // the owner. The database and the role are removed afterwards.
    private async Task WithManagedDatabaseAsync(Func<string, Task> action)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var database = "propr_managed_extension_" + suffix;
        var role = "propr_owner_" + suffix;
        var administrator = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
        var administratorOfDatabase = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database };
        var owner = new NpgsqlConnectionStringBuilder(administratorOfDatabase.ConnectionString)
        {
            Username = role,
            Password = OwnerPassword,
        };
        try
        {
            // CREATE ROLE takes no parameters, so the password is a literal with its single quotes doubled.
            var passwordLiteral = OwnerPassword.Replace("'", "''", StringComparison.Ordinal);
            await ExecuteAsync(administrator.ConnectionString, $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{passwordLiteral}';");
            await ExecuteAsync(administrator.ConnectionString, $"CREATE DATABASE \"{database}\" OWNER \"{role}\";");
            await ExecuteAsync(
                administratorOfDatabase.ConnectionString,
                """
                CREATE EXTENSION vector;
                CREATE FUNCTION refuse_create_extension() RETURNS event_trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'The server refuses CREATE EXTENSION.';
                END
                $$;
                CREATE EVENT TRIGGER refuse_create_extension ON ddl_command_start
                    WHEN TAG IN ('CREATE EXTENSION') EXECUTE FUNCTION refuse_create_extension();
                """);
            await action(owner.ConnectionString);
        }
        finally
        {
            // The role is dropped also when the drop of the database fails, so no run leaves a role behind.
            try
            {
                await ExecuteAsync(administrator.ConnectionString, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);");
            }
            finally
            {
                await ExecuteAsync(administrator.ConnectionString, $"DROP ROLE IF EXISTS \"{role}\";");
            }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
