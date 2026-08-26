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

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The upgrade that adds the month-and-author rollup and the mention job's native author column. An
///     installation upgrading has mention jobs already stored, and none of them can be given an identifier
///     afterwards, so the column has to arrive nullable and empty on those rows.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RollUpAuthorActivityByMonthMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "RecordReviewedPullRequestAuthor";
    private const string RollupMigration = "RollUpAuthorActivityByMonth";

    [Fact]
    public async Task Upgrading_AddsTheRollupAndLeavesStoredMentionJobsWithoutANativeAuthor()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_author_rollup_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        try
        {
            await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The scratch database keeps the historical schema away from the shared one. A server reached through
            // DB_CONNECTION_STRING may not grant the role CREATEDB, and that is a missing privilege rather than a
            // failure of the migration under test.
            Skip.If(
                true,
                "Skipping the author rollup migration test because the configured role may not CREATE DATABASE. "
                + "Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
            return;
        }

        try
        {
            var mentionId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                var clientId = await HistoricalSchemaSeed.SeedClientAsync(beforeUpgrade, "Author Rollup Migration");

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO mention_reply_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, thread_id, comment_id, mention_text, status, created_at, provider, code_review_platform_kind, comment_author_is_bot)
                    VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, '910', 9100, '@propr help', 'Completed', now(), 0, 0, false);
                    """,
                    mentionId,
                    clientId);
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, RollupMigration);

                // Read as a projection rather than as an entity: the schema here stops at this migration, while
                // the entity carries every column added since, and materializing it would select those too.
                Assert.Null(
                    await afterUpgrade.MentionReplyJobs.AsNoTracking()
                        .Where(row => row.Id == mentionId)
                        .Select(row => row.CommentAuthorNativeId)
                        .SingleAsync());

                Assert.True(await IsNullableAsync(afterUpgrade, "mention_reply_jobs", "comment_author_native_id"));

                // A stored row can be given the identifier later only by a write that carries one, so the column
                // has to accept one rather than only refuse null.
                await afterUpgrade.Database.ExecuteSqlRawAsync(
                    "UPDATE mention_reply_jobs SET comment_author_native_id = {0} WHERE id = {1};",
                    "vss-guid-91",
                    mentionId);

                var rollupKey = await ReadPrimaryKeyColumnsAsync(afterUpgrade, "licensing_author_activity");
                Assert.Equal(["activity_month", "author_key"], rollupKey);

                // The exclusion rule arrives separately and only sets flags, so the column has to default to a
                // counted author rather than leave the insert to state it.
                await afterUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO licensing_author_activity
                        (activity_month, author_key, provider, host_base_url, external_user_id, first_seen_at, first_seen_source)
                    VALUES (date_trunc('month', now() AT TIME ZONE 'UTC')::date, {0}, 0, 'https://dev.azure.com', 'vss-guid-91', now(), 0);
                    """,
                    "azuredevops|https://dev.azure.com|vss-guid-91");

                var recorded = await afterUpgrade.LicensingAuthorActivity.AsNoTracking().SingleAsync();
                Assert.False(recorded.Excluded);

                // The key is what makes a month hold an author once, so a second row for the same pair has to be
                // refused by the database and not only by the statement that writes it.
                var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                    afterUpgrade.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO licensing_author_activity
                            (activity_month, author_key, provider, host_base_url, external_user_id, first_seen_at, first_seen_source)
                        VALUES (date_trunc('month', now() AT TIME ZONE 'UTC')::date, {0}, 0, 'https://dev.azure.com', 'vss-guid-91', now(), 1);
                        """,
                        "azuredevops|https://dev.azure.com|vss-guid-91"));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    private static async Task<bool> IsNullableAsync(MeisterProPRDbContext dbContext, string table, string column)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT is_nullable = 'YES'
                  FROM information_schema.columns
                 WHERE table_name = @table AND column_name = @column
                """;
            command.Parameters.Add(new NpgsqlParameter("table", table));
            command.Parameters.Add(new NpgsqlParameter("column", column));

            return (bool?)await command.ExecuteScalarAsync() ?? false;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<List<string>> ReadPrimaryKeyColumnsAsync(MeisterProPRDbContext dbContext, string table)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT usage.column_name
                  FROM information_schema.table_constraints AS constraints
                  JOIN information_schema.key_column_usage AS usage
                    ON usage.constraint_name = constraints.constraint_name
                 WHERE constraints.table_name = @table AND constraints.constraint_type = 'PRIMARY KEY'
                 ORDER BY usage.ordinal_position
                """;
            command.Parameters.Add(new NpgsqlParameter("table", table));

            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }
        finally
        {
            await connection.CloseAsync();
        }
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
