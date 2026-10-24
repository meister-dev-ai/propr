// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class FindingRiskMigrationTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task UpgradePreservesHistoricalTotalsAndAllowsJointRiskCellsThenDowngradeClearsDerivedCache()
    {
        fixture.SkipIfUnavailable();
        const string previous = "20261018190955_RetainModelOpportunitiesAndHumanConcerns";
        var database = $"risk_migration_{Guid.NewGuid():N}";
        await fixture.CreateDatabaseAtMigrationAsync(database, previous);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>()
                    .UseNpgsql(connectionString, options => options.UseVector()).Options);
            var now = DateTimeOffset.UtcNow;
            var tenant = Guid.NewGuid();
            var client = Guid.NewGuid();
            var job = Guid.NewGuid();
            var date = DateOnly.FromDateTime(now.UtcDateTime);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                 VALUES ({tenant}, {database}, 'Risk migration', true, {now}, {now});
                 INSERT INTO clients (id, tenant_id, display_name, is_active, created_at)
                 VALUES ({client}, {tenant}, 'Risk migration', true, {now})
                 """);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO code_insight_daily_counts
                     (id, client_id, repository_id, pull_request_id, file_path, job_id, bucket_date, dimension, dimension_key, count, updated_at)
                 VALUES ({Guid.NewGuid()}, {client}, 'repo', 7, 'a.cs', {job}, {date}, 0, '', 7, {now})
                 """);
            await db.Database.MigrateAsync();
            var reader = new CodeInsightRollupReader(db);
            var query = new CodeInsightRollupQuery([client], date, date);
            Assert.Equal(7, await reader.GetTotalAsync(query));
            foreach (var dimension in new[] { CodeInsightCountDimension.Impact, CodeInsightCountDimension.Likelihood, CodeInsightCountDimension.RiskSeverity })
            {
                var point = Assert.Single(await reader.GetSeriesAsync(query, dimension, CodeInsightBucketSize.Day));
                Assert.Equal("Unknown", point.DimensionKey);
                Assert.Equal(7, point.Count);
            }

            db.CodeInsightDailyCounts.Add(
                new()
                {
                    Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 7, FilePath = "a.cs", JobId = job,
                    BucketDate = date, Dimension = CodeInsightCountDimension.FindingTotal, Count = 2, UpdatedAt = now,
                    Impact = FindingImpact.High, Likelihood = FindingLikelihood.Likely, RiskSeverity = FindingRiskSeverity.High,
                    RiskRubricVersion = "finding-risk-v1",
                });
            await db.SaveChangesAsync();
            Assert.Equal(9, await reader.GetTotalAsync(query));
            Assert.Equal(2, await reader.GetTotalAsync(query with { Impact = FindingImpact.High, RiskSeverity = FindingRiskSeverity.High }));
            Assert.Equal(7, await reader.GetTotalAsync(query with { Impact = FindingImpact.Unknown }));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM code_insight_daily_counts", connection);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
