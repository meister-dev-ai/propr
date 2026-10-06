// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection(nameof(ReviewerPerformanceAllocationCollection))]
public sealed class ReviewerPerformanceAllocationPostgresTests(PostgresContainerFixture fixture)
{
    [Fact]
    public Task MalformedRepositorySelectionsStayWithinTheirSmallPopulationAllocationBudget() =>
        this.AssertMalformedRepositorySelectionAllocations(repositoryCount: 4, allocationBudget: 32_000_000);

    [Fact]
    public Task MaximumLegalMalformedRepositorySelectionStaysWithinItsAllocationBudget() =>
        this.AssertMalformedRepositorySelectionAllocations(repositoryCount: 400, allocationBudget: 250_000_000);

    [Fact]
    public async Task MaximumLegalClientTimelinePreservesDenseCountsWithinItsAllocationBudget()
    {
        fixture.SkipIfUnavailable();
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.NewGuid();
        var clients = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        db.Tenants.Add(
            new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Performance test", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Clients.AddRange(
            clients.Select(id => new ClientRecord
            {
                Id = id, TenantId = tenant, DisplayName = "Timeline client", IsActive = true, CreatedAt = now
            }));
        db.CodeInsightPullRequests.AddRange(
            clients.SelectMany(client => Enumerable.Range(0, 50).Select(repository => new CodeInsightPullRequest
            {
                Id = Guid.NewGuid(), ClientId = client, RepositoryId = $"timeline-{repository}", RepositoryName = $"Repository {repository}", PullRequestId = 1,
                CreatedAt = now, UpdatedAt = now, PerformanceProjectionVersion = ReviewerPerformanceCountProjector.ProjectionVersion,
                PerformanceProjectedAt = now
            })));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO reviewer_performance_daily_counts
             ("Id", "CellKey", "CodeInsightPullRequestId", "ClientId", "RepositoryId", "ProviderScope", "PullRequestId", "BucketDate", "ModelId", "LogicalModelName", "TypeMembership", "Qualifier", "Outcome", "PublicationState", "DuplicateState", "IsMiss", "IsClassified", "Count", "UpdatedAt")
             SELECT gen_random_uuid(), md5(pr.id::text || '-' || d::text), pr.id, pr.client_id, pr.repository_id,
                 'AzureDevOps:https://dev.azure.com/test', 1, DATE '2026-01-01' + d, 'model', 'review', 'logic-error',
                 'Incorrect', 'positive', 1, 1, false, true, 1, {now}
             FROM code_insight_pull_requests pr CROSS JOIN generate_series(0,249) d WHERE pr.client_id = ANY({clients})
             """);
        var from = new DateOnly(2026, 1, 1);
        var query = new ReviewerPerformanceQuery
        {
            Bucket = "day", Aggregation = "cumulative", Grouping = "client",
            Views = [new() { From = from, To = from.AddDays(249), ClientIds = clients }]
        };
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, clients);
        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;

        var view = Assert.Single(result.Views);
        Assert.Equal(100_000, view.AggregateCells);
        Assert.Equal(8, view.Series.Count);
        Assert.Equal(2_000, view.Series.Sum(series => series.Points.Count));
        Assert.Equal(100_000, view.Series.Sum(series => series.Points[^1].Score.Counts.Outcomes.Positive));
        Assert.All(
            view.Series, series =>
            {
                Assert.Equal(250, series.Points.Count);
                for (var index = 0; index < series.Points.Count; index++)
                {
                    Assert.Equal(from.AddDays(index), series.Points[index].Date);
                    Assert.Equal(50L * (index + 1), series.Points[index].Score.Counts.Outcomes.Positive);
                }
            });
        Console.WriteLine(
            $"Client timeline: 100,000 cells, 400 repositories, 2,000 points, {timer.Elapsed.TotalMilliseconds:F0} ms, {allocated / 1_000_000d:F1} MB allocated.");
        Assert.True(allocated <= 1_000_000_000, $"The maximum client timeline allocated {allocated / 1_000_000d:F1} MB; budget <=1000 MB.");
    }

    private async Task AssertMalformedRepositorySelectionAllocations(int repositoryCount, long allocationBudget)
    {
        fixture.SkipIfUnavailable();
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Selector allocation test", IsActive = true, CreatedAt = now, UpdatedAt = now
            });
        db.Clients.Add(new ClientRecord { Id = client, TenantId = tenant, DisplayName = "Selector client", IsActive = true, CreatedAt = now });
        db.CodeInsightPullRequests.AddRange(
            Enumerable.Range(0, repositoryCount).Select(repository => new CodeInsightPullRequest
            {
                Id = Guid.NewGuid(), ClientId = client, RepositoryId = $"selector-{repository}", RepositoryName = $"Repository {repository}", PullRequestId = 1,
                CreatedAt = now, UpdatedAt = now, PerformanceProjectionVersion = ReviewerPerformanceCountProjector.ProjectionVersion,
                PerformanceProjectedAt = now
            }));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO reviewer_performance_daily_counts
             ("Id", "CellKey", "CodeInsightPullRequestId", "ClientId", "RepositoryId", "ProviderScope", "PullRequestId", "BucketDate", "ModelId", "LogicalModelName", "TypeMembership", "Qualifier", "Outcome", "PublicationState", "DuplicateState", "IsMiss", "IsClassified", "Count", "UpdatedAt")
             SELECT gen_random_uuid(), md5(pr.id::text || '-' || d::text), pr.id, pr.client_id, pr.repository_id,
                 '', 1, DATE '2026-01-01' + d, 'model', 'review', 'logic-error',
                 'Missing', 'missActed', 1, 1, true, true, 1, {now}
             FROM code_insight_pull_requests pr CROSS JOIN generate_series(0,249) d WHERE pr.client_id = {client}
             """);
        var malformedSelectors = Enumerable.Range(0, 64).Select(index => index % 2 == 0
            ? $"!invalid-{index}"
            : Convert.ToBase64String(Encoding.UTF8.GetBytes($"[invalid-{index}"))).ToArray();
        var from = new DateOnly(2026, 1, 1);
        var query = new ReviewerPerformanceQuery
        {
            Bucket = "day", Aggregation = "cumulative", Grouping = "none",
            Views = [new() { From = from, To = from.AddDays(249), ClientIds = [client], Repositories = [.. malformedSelectors, .. malformedSelectors] }]
        };
        var reader = new ReviewerPerformanceRangeReader(db);
        // Warm the public query path without decoding malformed selectors before measuring process-wide allocations.
        await reader.QueryAsync(query with { Views = [query.Views[0] with { Repositories = [] }] }, [client]);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);

        var result = await reader.QueryAsync(query, [client]);

        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var view = Assert.Single(result.Views);
        Assert.Equal(repositoryCount * 250, view.AggregateCells);
        var series = Assert.Single(view.Series);
        Assert.Equal(250, series.Points.Count);
        Assert.All(series.Points, point => Assert.Equal(0, point.Score.Counts.HarvestedThreads));
        Console.WriteLine($"Malformed repository selection: {view.AggregateCells:N0} cells, 128 selectors, {allocated / 1_000_000d:F1} MB allocated.");
        Assert.True(
            allocated <= allocationBudget,
            $"The malformed repository selection allocated {allocated / 1_000_000d:F1} MB; budget <={allocationBudget / 1_000_000d:F0} MB.");
    }
}
