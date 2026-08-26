// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     The upgrade that adds the reviewed pull request's author to the review job. Every column has to arrive
///     nullable and a row written before the upgrade has to come through it empty, because the author is known
///     only from a fetch and a job that has already run will not be fetched again.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RecordReviewedPullRequestAuthorMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "RemoveRunnerSlotEntitlement";
    private const string AuthorMigration = "RecordReviewedPullRequestAuthor";

    private static readonly string[] AuthorColumns =
    [
        "pr_author_display_name",
        "pr_author_external_user_id",
        "pr_author_is_bot",
        "pr_author_login",
    ];

    [Fact]
    public async Task Upgrading_AddsTheAuthorColumnsNullableAndLeavesExistingRowsEmpty()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_pr_author_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        try
        {
            await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The scratch database keeps the historical schema away from the shared one. A server reached
            // through DB_CONNECTION_STRING may not grant the role CREATEDB, which is a missing privilege rather
            // than a failure of the migration under test.
            Skip.If(
                true,
                "Skipping the pull request author migration test because the configured role may not CREATE DATABASE. "
                + "Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
            return;
        }

        try
        {
            var jobId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "PR Author Migration Test");

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, iteration_id, status, submitted_at, retry_count, cost_is_approximate, allow_unchanged_resubmission)
                    VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, 1, 2, now(), 0, false, false);
                    """,
                    jobId,
                    clientId);
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, AuthorMigration);

                foreach (var column in AuthorColumns)
                {
                    Assert.True(
                        await IsNullableColumnAsync(scratch, column),
                        $"Column {column} must exist on review_jobs and accept null.");
                }

                // Read with raw SQL rather than through the entity: this database stops at the migration under
                // test, while the model carries every column added since.
                var author = await ReadAuthorAsync(scratch, jobId);
                Assert.Null(author.ExternalUserId);
                Assert.Null(author.Login);
                Assert.Null(author.DisplayName);
                Assert.Null(author.IsBot);
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    private static async Task<bool> IsNullableColumnAsync(string connectionString, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_name = 'review_jobs' AND column_name = @column;
            """,
            connection);
        command.Parameters.AddWithValue("column", column);

        var isNullable = await command.ExecuteScalarAsync();
        return string.Equals(isNullable as string, "YES", StringComparison.Ordinal);
    }

    private static async Task<(string? ExternalUserId, string? Login, string? DisplayName, bool? IsBot)> ReadAuthorAsync(
        string connectionString,
        Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pr_author_external_user_id, pr_author_login, pr_author_display_name, pr_author_is_bot
            FROM review_jobs WHERE id = @id;
            """,
            connection);
        command.Parameters.AddWithValue("id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3) ? null : reader.GetBoolean(3));
    }

    private static MeisterProPRDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static Task MigrateToAsync(MeisterProPRDbContext dbContext, string targetMigration)
    {
        return dbContext.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task ExecuteOnServerAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
