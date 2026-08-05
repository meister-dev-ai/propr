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
///     An absent thread watermark differs from every current revision, so a deployment that added the column
///     without seeding it would fire a thread pass for every open pull request on its first tick.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AddThreadPassMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "AllowUnchangedResubmissionOnReviewJob";
    private const string ThreadPassMigration = "AddThreadPass";

    [Fact]
    public async Task Upgrading_SeedsTheThreadWatermarkFromTheReviewWatermark()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_thread_pass_migration_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var clientId = Guid.NewGuid();

            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);

                var tenantId = await beforeUpgrade.Tenants
                    .Select(tenant => tenant.Id)
                    .FirstOrDefaultAsync();
                if (tenantId == Guid.Empty)
                {
                    tenantId = TenantCatalog.SystemTenantId;
                    beforeUpgrade.Tenants.Add(
                        new TenantRecord
                        {
                            Id = tenantId,
                            Slug = "migration-test",
                            DisplayName = "Migration Test Tenant",
                            IsActive = true,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                }

                beforeUpgrade.Clients.Add(
                    new ClientRecord
                    {
                        Id = clientId,
                        TenantId = tenantId,
                        DisplayName = "Migration Test Client",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                await beforeUpgrade.SaveChangesAsync();

                // Written through raw SQL because the entity already carries the column this migration adds.
                await beforeUpgrade.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_pr_scans (id, client_id, repository_id, pull_request_id, last_processed_commit_id, updated_at)
                    VALUES ({0}, {1}, 'repo-migration', 91, 'iteration-11', now());
                    """,
                    Guid.NewGuid(),
                    clientId);
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, ThreadPassMigration);

                var scan = await afterUpgrade.ReviewPrScans
                    .AsNoTracking()
                    .FirstAsync(candidate => candidate.ClientId == clientId);

                Assert.Equal("iteration-11", scan.LastProcessedCommitId);
                Assert.Equal("iteration-11", scan.LastThreadPassRevisionKey);
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminBuilder.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
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
