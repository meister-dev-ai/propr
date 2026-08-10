// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
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
///     The upgrade that puts the revision into the answered-thread key and moves the in-flight claim into the
///     database. Rows written before either existed have to survive it, and a pull request that the old claim
///     let two passes hold has to end up held by one.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ThreadPassIdempotencyMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "ThreadPassSpendAndTrace";
    private const string IdempotencyMigration = "ThreadPassIdempotencyAndClaims";

    [Fact]
    public async Task Upgrading_LeavesAnAlreadyAnsweredThreadAssessableAgainAtTheNextRevision()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_thread_pass_idempotency_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var passId = Guid.NewGuid();
            var handledId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                var clientId = await SeedClientAsync(beforeUpgrade);
                await InsertPassAsync(beforeUpgrade, passId, clientId, "7|aaa", "Completed");

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO thread_pass_handled_threads (id, thread_pass_job_id, client_id, repository_id, pull_request_id, thread_id, observed_reply_count, recorded_at)
                    VALUES ({0}, {1}, {2}, 'repo', 91, 17, 0, now());
                    """,
                    handledId,
                    passId,
                    clientId);
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, IdempotencyMigration);

                // Read as a column rather than as the entity: this stops at the migration under test, where the
                // schema is deliberately older than the model the rest of the suite maps against.
                var storedRevisionKey = await afterUpgrade.Database
                    .SqlQuery<string>($"SELECT revision_key AS \"Value\" FROM thread_pass_handled_threads WHERE id = {handledId}")
                    .SingleAsync();

                // No revision was recorded at the time, so the row matches no revision a pass ever runs at and
                // suppresses nothing. A finding it had made permanently unassessable is judged again.
                Assert.Equal(string.Empty, storedRevisionKey);
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task Upgrading_LeavesOnePassHoldingAPullRequestTwoWereHolding()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_thread_pass_claim_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var older = Guid.NewGuid();
            var newer = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                var clientId = await SeedClientAsync(beforeUpgrade);

                // What the read-then-insert claim allowed: two writers with different trigger states, both
                // holding one pull request.
                await InsertPassAsync(beforeUpgrade, older, clientId, "7|aaa", "Pending", createdAtOffsetMinutes: -5);
                await InsertPassAsync(beforeUpgrade, newer, clientId, "8|bbb", "Pending");
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, IdempotencyMigration);

                var stored = await afterUpgrade.ThreadPassJobs.AsNoTracking().ToListAsync();
                Assert.Equal(
                    ThreadPassJobStatus.Cancelled,
                    stored.First(candidate => candidate.Id == older).Status);
                Assert.Equal(
                    ThreadPassJobStatus.Pending,
                    stored.First(candidate => candidate.Id == newer).Status);
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    private static Task InsertPassAsync(
        MeisterProPRDbContext dbContext,
        Guid passId,
        Guid clientId,
        string triggerKey,
        string status,
        int createdAtOffsetMinutes = 0)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO thread_pass_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, iteration_id, revision_key, trigger_key, provider, code_review_platform_kind, status, attempt_count, created_at, total_input_tokens, total_output_tokens, cost_is_approximate)
            VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, 7, {2}, {3}, 0, 0, {4}, 0, now() + make_interval(mins => {5}), 0, 0, false);
            """,
            passId,
            clientId,
            triggerKey.Split('|')[0],
            triggerKey,
            status,
            createdAtOffsetMinutes);
    }

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext dbContext)
    {
        return await HistoricalSchemaSeed.SeedClientAsync(dbContext, "Thread Pass Idempotency Migration Test");
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
