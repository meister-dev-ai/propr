// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
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
///     The upgrade that turns every stored thread identifier into the provider's own text form. An Azure DevOps
///     installation's identifiers are already the provider's own, so the digits have to arrive unchanged and
///     every key has to keep naming the thread it named before.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ProviderNativeThreadIdentityMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "ThreadPassIdempotencyAndClaims";
    private const string IdentityMigration = "ProviderNativeThreadIdentity";

    /// <summary>A thread id with more digits than another, so a length-sensitive rewrite would show up.</summary>
    private const long LongerAzureThreadId = 1_284_003;

    private const long ShorterAzureThreadId = 9;

    [Fact]
    public async Task Upgrading_LeavesEveryAzureDevOpsThreadKeyNamingTheSameThread()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_thread_identity_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var scanId = Guid.NewGuid();
            var passId = Guid.NewGuid();
            var memoryId = Guid.NewGuid();
            var mentionId = Guid.NewGuid();
            var postedFindingId = Guid.NewGuid();
            var activityId = Guid.NewGuid();
            var handledId = Guid.NewGuid();
            Guid clientId;

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                clientId = await SeedClientAsync(beforeUpgrade);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_pr_scans (id, client_id, repository_id, pull_request_id, last_processed_commit_id, last_thread_pass_revision_key, updated_at)
                    VALUES ({0}, {1}, 'repo', 91, 'iter-1', '', now());

                    INSERT INTO review_pr_scan_threads (review_pr_scan_id, thread_id, last_seen_reply_count, last_seen_status)
                    VALUES ({0}, {2}, 3, 'Active'), ({0}, {3}, 1, 'Fixed');
                    """,
                    scanId,
                    clientId,
                    LongerAzureThreadId,
                    ShorterAzureThreadId);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO thread_pass_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, iteration_id, revision_key, trigger_key, provider, code_review_platform_kind, status, attempt_count, created_at, total_input_tokens, total_output_tokens, cost_is_approximate)
                    VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, 7, '7|aaa', '7|aaa', 0, 0, 'Completed', 1, now(), 0, 0, false);

                    INSERT INTO thread_pass_handled_threads (id, thread_pass_job_id, client_id, repository_id, pull_request_id, thread_id, observed_reply_count, revision_key, recorded_at)
                    VALUES ({2}, {0}, {1}, 'repo', 91, {3}, 0, '7|aaa', now());
                    """,
                    passId,
                    clientId,
                    handledId,
                    LongerAzureThreadId);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO thread_memory_records (id, client_id, thread_id, repository_id, pull_request_id, file_path, change_excerpt, comment_history_digest, resolution_summary, embedding_vector, created_at, updated_at, memory_source, keywords)
                    VALUES ({0}, {1}, {2}, 'repo', 91, 'src/Foo.cs', NULL, 'digest', 'The retry count was restored.', array_fill(0.1::real, ARRAY[1536])::vector, now(), now(), 0, ARRAY[]::text[]);

                    INSERT INTO memory_activity_log (id, client_id, thread_id, repository_id, pull_request_id, action, current_status, previous_status, reason, occurred_at)
                    VALUES ({3}, {1}, {2}, 'repo', 91, 0, 'resolved', NULL, NULL, now());
                    """,
                    memoryId,
                    clientId,
                    LongerAzureThreadId,
                    activityId);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO mention_reply_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, thread_id, comment_id, mention_text, status, created_at, provider, code_review_platform_kind, comment_author_is_bot)
                    VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, {2}, 55, '@propr help', 'Pending', now(), 0, 0, false);

                    INSERT INTO posted_finding_records (id, client_id, repository_id, pull_request_id, provider_thread_id, review_job_id, iteration_id, file_path, severity, finding_message, embedding_vector, created_at, auto_resolved_by_propr)
                    VALUES ({3}, {1}, 'repo', 91, {2}, {4}, 7, 'src/Foo.cs', 2, 'The delete path races.', array_fill(0.1::real, ARRAY[1536])::vector, now(), false);
                    """,
                    mentionId,
                    clientId,
                    LongerAzureThreadId,
                    postedFindingId,
                    Guid.NewGuid());
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, IdentityMigration);

                var longer = LongerAzureThreadId.ToString(CultureInfo.InvariantCulture);
                var shorter = ShorterAzureThreadId.ToString(CultureInfo.InvariantCulture);

                // The composite primary key: both rows survive, and each one is still found under its own id.
                var scanThreads = await afterUpgrade.ReviewPrScanThreads
                    .AsNoTracking()
                    .Where(thread => thread.ReviewPrScanId == scanId)
                    .ToListAsync();
                Assert.Equal(2, scanThreads.Count);
                Assert.Equal(3, scanThreads.Single(thread => thread.ThreadId == longer).LastSeenReplyCount);
                Assert.Equal(1, scanThreads.Single(thread => thread.ThreadId == shorter).LastSeenReplyCount);

                Assert.Equal(
                    longer,
                    (await afterUpgrade.ThreadPassHandledThreads.AsNoTracking()
                        .SingleAsync(row => row.Id == handledId)).ThreadId);
                Assert.Equal(
                    longer,
                    (await afterUpgrade.ThreadMemoryRecords.AsNoTracking()
                        .SingleAsync(row => row.Id == memoryId)).ThreadId);
                Assert.Equal(
                    longer,
                    (await afterUpgrade.MemoryActivityLogEntries.AsNoTracking()
                        .SingleAsync(row => row.Id == activityId)).ThreadId);
                Assert.Equal(
                    longer,
                    (await afterUpgrade.MentionReplyJobs.AsNoTracking()
                        .SingleAsync(row => row.Id == mentionId)).ThreadId);
                Assert.Equal(
                    longer,
                    (await afterUpgrade.PostedFindingRecords.AsNoTracking()
                        .SingleAsync(row => row.Id == postedFindingId)).ProviderThreadId);

                // The unique keys were rebuilt over the new column type, so the constraint that stops a second
                // row for one thread still bites.
                var duplicate = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await afterUpgrade.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO posted_finding_records (id, client_id, repository_id, pull_request_id, provider_thread_id, review_job_id, iteration_id, file_path, severity, finding_message, embedding_vector, created_at, auto_resolved_by_propr)
                        VALUES ({0}, {1}, 'repo', 91, {2}, {3}, 8, 'src/Foo.cs', 2, 'A second row for one thread.', array_fill(0.1::real, ARRAY[1536])::vector, now(), false);
                        """,
                        Guid.NewGuid(),
                        clientId,
                        longer,
                        Guid.NewGuid());
                });
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

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext dbContext)
    {
        var tenantId = await dbContext.Tenants.Select(tenant => tenant.Id).FirstOrDefaultAsync();
        if (tenantId == Guid.Empty)
        {
            tenantId = TenantCatalog.SystemTenantId;
            dbContext.Tenants.Add(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = "thread-identity-migration-test",
                    DisplayName = "Thread Identity Migration Test Tenant",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
        }

        var clientId = Guid.NewGuid();
        dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = tenantId,
                DisplayName = "Thread Identity Migration Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await dbContext.SaveChangesAsync();
        return clientId;
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
