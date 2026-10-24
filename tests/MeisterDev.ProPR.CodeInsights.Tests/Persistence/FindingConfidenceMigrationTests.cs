// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class FindingConfidenceMigrationTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task UpgradePreservesJointTotalsAndDowngradeRebuildsDerivedCellsWithoutDeletingReports()
    {
        fixture.SkipIfUnavailable();
        const string previous = "20261020055116_AddFindingRiskAssessments";
        var database = $"confidence_migration_{Guid.NewGuid():N}";
        await fixture.CreateDatabaseAtMigrationAsync(database, previous);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using var db = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>()
                    .UseNpgsql(connectionString, options => options.UseVector()).Options);
            var now = DateTimeOffset.UtcNow;
            var client = Guid.NewGuid();
            var tenant = Guid.NewGuid();
            var job = Guid.NewGuid();
            var date = DateOnly.FromDateTime(now.UtcDateTime);
            var report = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                 VALUES ({tenant}, {database}, 'Confidence migration', true, {now}, {now});
                 INSERT INTO clients (id, tenant_id, display_name, is_active, created_at)
                 VALUES ({client}, {tenant}, 'Confidence migration', true, {now})
                 """);
            db.Add(
                new ReviewerPerformanceReport
                {
                    Id = report, Name = "Captured", CalculationVersion = "reviewer-performance-v2", CapturedAt = now,
                    ExpiresAt = now.AddDays(1), Payload = "{}", RequestFingerprint = "captured"
                });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO code_insight_daily_counts
                     (id, client_id, repository_id, pull_request_id, file_path, job_id, bucket_date, dimension, dimension_key, count, updated_at,
                      "Impact", "Likelihood", "RiskSeverity", "RiskRubricVersion")
                 VALUES ({Guid.NewGuid()}, {client}, 'repo', 7, 'a.cs', {job}, {date}, 0, '', 7, {now}, 0, 0, 0, ''),
                        ({Guid.NewGuid()}, {client}, 'repo', 7, 'a.cs', {job}, {date}, 0, '', 2, {now}, 3, 1, 2, 'finding-risk-v1')
                 """);
            await db.Database.MigrateAsync();
            var reader = new CodeInsightRollupReader(db);
            var scope = new CodeInsightRollupQuery([client], date, date);
            Assert.Equal(9, await reader.GetTotalAsync(scope));
            var historical = Assert.Single(await reader.GetSeriesAsync(scope, CodeInsightCountDimension.Confidence, CodeInsightBucketSize.Day));
            Assert.Equal("Unknown", historical.DimensionKey);
            Assert.Equal(9, historical.Count);
            Assert.Equal(2, await reader.GetTotalAsync(scope with { Impact = FindingImpact.High, Confidence = FindingConfidence.Unknown }));
            foreach (var confidence in new[] { FindingConfidence.Low, FindingConfidence.Certain })
            {
                db.CodeInsightDailyCounts.Add(
                    new()
                    {
                        Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 7, FilePath = "a.cs", JobId = job,
                        BucketDate = date, Dimension = CodeInsightCountDimension.FindingTotal, Count = 1, UpdatedAt = now,
                        Impact = FindingImpact.High, Likelihood = FindingLikelihood.Unlikely, RiskSeverity = FindingRiskSeverity.Medium,
                        RiskRubricVersion = "finding-risk-v1", Confidence = confidence,
                    });
            }

            await db.SaveChangesAsync();
            Assert.Equal(11, await reader.GetTotalAsync(scope));
            Assert.Equal(1, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Certain }));
            var aggregate = new CodeInsightPullRequest
            {
                Id = Guid.NewGuid(), ClientId = client, RepositoryId = "repo", PullRequestId = 7, LastActivityAt = now,
                PerformanceProjectionVersion = 6, PerformanceProjectedAt = now,
            };
            db.Add(aggregate);
            var codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
            var assessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Unlikely, "A record is lost.", "Rare input.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path proves the loss.");
            var encrypted = CodeInsightRiskAssessmentProtection.Protect(assessment, codec);
            db.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, JobId = job, RevisionKey = "revision", Ordinal = 0,
                    FilePath = "a.cs", LineNumber = 10, Severity = CommentSeverity.Error, ObservedAt = now, CreatedAt = now,
                    EncryptedMessage = codec.Protect("The update loses the record.", "code-insight-finding-message"),
                    Confidence = FindingConfidence.Certain, EncryptedRiskAssessment = encrypted, Impact = assessment.Impact,
                    Likelihood = assessment.Likelihood, RiskSeverity = assessment.Severity, RiskRubricVersion = assessment.RubricVersion!,
                });
            db.Add(
                new ReviewerPerformanceDailyCount
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ClientId = client, RepositoryId = "repo", PullRequestId = 7,
                    BucketDate = date, Confidence = FindingConfidence.Certain, CellKey = "captured", Outcome = "positive", Count = 1, UpdatedAt = now,
                });
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var counts = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM code_insight_daily_counts) + (SELECT count(*) FROM reviewer_performance_daily_counts)", connection);
            Assert.Equal(0L, await counts.ExecuteScalarAsync());
            await using var snapshot = new NpgsqlCommand("SELECT \"Payload\" FROM reviewer_performance_reports WHERE \"Id\" = @id", connection);
            snapshot.Parameters.AddWithValue("id", report);
            Assert.Equal("{}", await snapshot.ExecuteScalarAsync());
            await using var projection = new NpgsqlCommand(
                "SELECT \"PerformanceProjectionVersion\" FROM code_insight_pull_requests WHERE id = @id", connection);
            projection.Parameters.AddWithValue("id", aggregate.Id);
            Assert.Equal(0, await projection.ExecuteScalarAsync());

            await db.Database.MigrateAsync();
            db.ChangeTracker.Clear();
            var gate = Substitute.For<ICodeInsightsCollectionGate>();
            gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            var quality = new CodeInsightRollupProjector(db, gate, NullLogger<CodeInsightRollupProjector>.Instance);
            await quality.ProjectJobAsync(job);
            Assert.Equal(1, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Unknown }));
            using var services = new ServiceCollection().AddSingleton(db).AddSingleton(gate)
                .AddSingleton<ISecretProtectionCodec>(codec)
                .AddSingleton<ILogger<ReviewerPerformanceCountProjector>>(NullLogger<ReviewerPerformanceCountProjector>.Instance)
                .AddTransient<ReviewerPerformanceCountProjector>().BuildServiceProvider();
            var performance = services.GetRequiredService<ReviewerPerformanceCountProjector>();
            var catchUp = new CodeInsightRollupProjector(db, gate, NullLogger<CodeInsightRollupProjector>.Instance, performanceProjector: performance);
            await catchUp.BackfillAsync(10);
            var restored = Assert.Single(await db.CodeInsightFindings.AsNoTracking().ToListAsync());
            Assert.Equal(FindingConfidence.Certain, restored.Confidence);
            Assert.Equal(encrypted, restored.EncryptedRiskAssessment);
            Assert.Equal(1, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Certain }));
            Assert.Equal(0, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Unknown }));
            Assert.Equal(
                "Certain", Assert.Single(await reader.GetSeriesAsync(scope, CodeInsightCountDimension.Confidence, CodeInsightBucketSize.Day)).DimensionKey);
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
