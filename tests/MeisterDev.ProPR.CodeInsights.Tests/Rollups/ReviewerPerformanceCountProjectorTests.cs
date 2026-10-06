// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Rollups;

public sealed class ReviewerPerformanceCountProjectorTests
{
    [Fact]
    public async Task UnknownNativeEvidenceIsRetainedSeparatelyFromProvenOpenThreads()
    {
        await using var db = CreateDb();
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        db.Add(pr);
        foreach (var status in new string?[] { "Active", "Pending", "Unknown", "Unexpected", null, "" })
        {
            db.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), ObservedAt = DateTimeOffset.UtcNow,
                    ProviderThreadId = Guid.NewGuid().ToString(), PublicationState = CodeInsightPublicationState.Published, NativeStatus = status
                });
        }

        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        await new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance).ProjectPullRequestAsync(pr.Id);
        var counts = await db.ReviewerPerformanceDailyCounts.Where(row => !row.Outcome.StartsWith("collection")).ToListAsync();
        Assert.Equal(2, counts.Where(row => row.Outcome == "unresolved").Sum(row => row.Count));
        Assert.Equal(4, counts.Where(row => row.Outcome == "unknown").Sum(row => row.Count));
    }

    [Fact]
    public async Task ReprojectionRetainsJointMembershipAndRefreshesTheOriginalCohort()
    {
        await using var db = CreateDb();
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), RevisionKey = "revision",
            ObservedAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero),
            PublicationState = CodeInsightPublicationState.Published, ProviderCommentId = "comment", ProviderThreadId = "thread",
            OriginModelId = "actual-model", Qualifier = CodeInsightFindingQualifier.Missing,
        };
        db.AddRange(pr, finding);
        db.CodeInsightFindingTags.AddRange(
            new CodeInsightFindingTag { Id = Guid.NewGuid(), CodeInsightFindingId = finding.Id, IsCore = true, CoreSlug = "logic-error" },
            new CodeInsightFindingTag { Id = Guid.NewGuid(), CodeInsightFindingId = finding.Id, IsCore = true, CoreSlug = "concurrency" });
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        await projector.ProjectPullRequestAsync(pr.Id);
        Assert.Equal(
            "unknown", Assert.Single(await db.ReviewerPerformanceDailyCounts.Where(row => !row.Outcome.StartsWith("collection")).ToListAsync()).Outcome);

        db.CodeInsightFindingDispositions.Add(
            new CodeInsightFindingDisposition
            {
                Id = Guid.NewGuid(), CodeInsightFindingId = finding.Id, Disposition = CodeInsightDisposition.Acknowledged,
                NativeStatus = "WontFix", DecidedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        await projector.ProjectPullRequestAsync(pr.Id);
        await projector.ProjectPullRequestAsync(pr.Id);

        var cell = Assert.Single(await db.ReviewerPerformanceDailyCounts.Where(row => !row.Outcome.StartsWith("collection")).ToListAsync());
        Assert.Equal(1, cell.Count);
        Assert.Equal(new DateOnly(2026, 9, 4), cell.BucketDate);
        Assert.Equal("wontFix", cell.Outcome);
        Assert.Equal("concurrency|logic-error", cell.TypeMembership);
        Assert.Equal("actual-model", cell.ModelId);
    }

    [Fact]
    public async Task ClassifierFailureAndSuppressionCannotCreateScoredSuccesses()
    {
        await using var db = CreateDb();
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        var finding = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), PublicationState = CodeInsightPublicationState.SuppressedRepeat,
            ObservedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(pr, finding);
        db.Add(
            new CodeInsightFindingDisposition
            {
                Id = Guid.NewGuid(), CodeInsightFindingId = finding.Id, Disposition = CodeInsightDisposition.Dismissed, NativeStatus = "Fixed",
                ClassifierVersion = "failed", ClassifierConfidence = null
            });
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        await new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance).ProjectPullRequestAsync(pr.Id);
        var cell = Assert.Single(await db.ReviewerPerformanceDailyCounts.Where(row => !row.Outcome.StartsWith("collection")).ToListAsync());
        Assert.Equal("unknown", cell.Outcome);
        Assert.Equal(CodeInsightPublicationState.SuppressedRepeat, cell.PublicationState);
    }

    [Fact]
    public async Task ReviewExposureWithoutFindingsOrHarvestRetainsUnknownCollectionCohort()
    {
        await using var db = CreateDb();
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "empty", PullRequestId = 1 };
        db.AddRange(
            pr,
            new CodeInsightReviewExposure
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), FilePath = "src/Service.cs",
                ProviderScope = "GitHub:https://github.example", ModelId = "review-model", ObservedAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)
            });
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        await new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance).ProjectPullRequestAsync(pr.Id);
        var cell = Assert.Single(await db.ReviewerPerformanceDailyCounts.ToListAsync());
        Assert.Equal("collectionUnknown", cell.Outcome);
        Assert.Equal(new DateOnly(2026, 9, 1), cell.BucketDate);
    }

    [Fact]
    public async Task SuccessfulHarvestsInSeparateNamespacesRetainIndependentCoverageIncludingEmptyHarvests()
    {
        await using var db = CreateDb();
        var key = new CodeInsightPullRequestKey(Guid.NewGuid(), "1", 1);
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var recorder = new MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightHarvestCoverageRecorder(
            db, gate, projector, NullLogger<MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightHarvestCoverageRecorder>.Instance);
        var now = DateTimeOffset.UtcNow;
        await recorder.RecordAsync(key, "AzureDevOps:https://dev.azure.com/orgA", true, now);
        await recorder.RecordAsync(key, "AzureDevOps:https://dev.azure.com/orgB", true, now);
        var cells = await db.ReviewerPerformanceDailyCounts.ToListAsync();
        Assert.Equal(2, cells.Count);
        Assert.All(cells, row => Assert.Equal("collectionComplete", row.Outcome));
        Assert.Equal(2, cells.Select(row => row.ProviderScope).Distinct().Count());
    }

    private static MeisterProPRDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
