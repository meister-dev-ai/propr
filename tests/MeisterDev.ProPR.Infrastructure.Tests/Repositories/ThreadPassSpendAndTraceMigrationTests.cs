// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
///     A trace record now belongs to a review job or to a thread pass. The owner column that already carried
///     every existing row must keep carrying it, and a row with no owner or two is unreachable from one read
///     path and counted twice by the other.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ThreadPassSpendAndTraceMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "AddThreadPass";
    private const string SpendMigration = "ThreadPassSpendAndTrace";

    [Fact]
    public async Task Upgrading_KeepsExistingTracesOwnedByTheirReviewJob()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_thread_pass_spend_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var jobId = Guid.NewGuid();
            var protocolId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                var clientId = await SeedClientAsync(beforeUpgrade);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_jobs (id, client_id, organization_url, project_id, repository_id, pull_request_id, iteration_id, status, submitted_at, retry_count, cost_is_approximate, allow_unchanged_resubmission)
                    VALUES ({0}, {1}, 'https://dev.azure.com/org', 'proj', 'repo', 91, 1, 2, now(), 0, false, false);
                    """,
                    jobId,
                    clientId);

                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_job_protocols (id, job_id, attempt_number, started_at, cache_observability)
                    VALUES ({0}, {1}, 1, now(), 0);
                    """,
                    protocolId,
                    jobId);
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, SpendMigration);

                var stored = await afterUpgrade.ReviewJobProtocols
                    .AsNoTracking()
                    .FirstAsync(candidate => candidate.Id == protocolId);

                Assert.Equal(jobId, stored.JobId);
                Assert.Null(stored.ThreadPassJobId);

                var ownerless = await Record.ExceptionAsync(() => afterUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_job_protocols (id, attempt_number, started_at, cache_observability)
                    VALUES ({0}, 1, now(), 0);
                    """,
                    Guid.NewGuid()));
                Assert.IsType<PostgresException>(ownerless);
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
                    Slug = "spend-migration-test",
                    DisplayName = "Spend Migration Test Tenant",
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
                DisplayName = "Spend Migration Test Client",
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
