// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Rollups;

public sealed class ReviewerPerformanceCountProjectorTests
{
    [Theory]
    [InlineData("invalid-json")]
    [InlineData("other-key")]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task MissingOrUnreadableOptionalAssessmentDoesNotBlockTheAggregateOrLaterCatchUpWork(string assessmentState)
    {
        await using var db = CreateDb();
        var codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
        var otherCodec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
        var risk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Unlikely, "A record is lost.", "Rare input.",
            "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path proves the loss.");
        var unreadable = assessmentState switch
        {
            "invalid-json" => codec.Protect("{", "code-insight-finding-risk-assessment"),
            "other-key" => CodeInsightRiskAssessmentProtection.Protect(risk, otherCodec),
            "missing" => null,
            "empty" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(assessmentState)),
        };
        var first = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1, PerformanceProjectionVersion = 5,
            PerformanceProjectedAt = DateTimeOffset.UnixEpoch
        };
        var second = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = first.ClientId, RepositoryId = "repo", PullRequestId = 2, PerformanceProjectionVersion = 5,
            PerformanceProjectedAt = DateTimeOffset.UnixEpoch.AddDays(1)
        };
        db.AddRange(first, second);
        var bad = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = first.Id, JobId = Guid.NewGuid(), ObservedAt = DateTimeOffset.UtcNow,
            EncryptedRiskAssessment = unreadable
        };
        var good = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = second.Id, JobId = Guid.NewGuid(), ObservedAt = DateTimeOffset.UtcNow,
            EncryptedRiskAssessment = CodeInsightRiskAssessmentProtection.Protect(risk, codec)
        };
        db.AddRange(bad, good);
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(gate)
            .AddSingleton<ISecretProtectionCodec>(codec)
            .AddSingleton<ILogger<ReviewerPerformanceCountProjector>>(NullLogger<ReviewerPerformanceCountProjector>.Instance)
            .AddTransient<ReviewerPerformanceCountProjector>().BuildServiceProvider();
        var projector = services.GetRequiredService<ReviewerPerformanceCountProjector>();
        Assert.Equal(2, await projector.BackfillAsync(2));
        Assert.Equal(6, first.PerformanceProjectionVersion);
        Assert.Equal(6, second.PerformanceProjectionVersion);
        Assert.Equal(FindingConfidence.Unknown, bad.Confidence);
        Assert.Equal(FindingConfidence.Certain, good.Confidence);
        Assert.Equal(unreadable, bad.EncryptedRiskAssessment);
        Assert.Contains(
            await db.ReviewerPerformanceDailyCounts.ToListAsync(),
            cell => cell.CodeInsightPullRequestId == first.Id && cell.Confidence == FindingConfidence.Unknown);
        Assert.Equal(0, await projector.BackfillAsync(2));
    }

    [Fact]
    public async Task BoundedCatchUpRestoresQueryableConfidenceFromRetainedEvidenceAndRefreshesQualityCounts()
    {
        await using var db = CreateDb();
        var codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
        var date = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var pr = new CodeInsightPullRequest
            { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1, PerformanceProjectionVersion = 5 };
        db.Add(pr);
        var risk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Unlikely, "A record is lost.", "Rare input.",
            "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path proves the loss.");
        var retained = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), ObservedAt = date,
            Confidence = FindingConfidence.Unknown, Impact = risk.Impact, Likelihood = risk.Likelihood, RiskSeverity = risk.Severity,
            RiskRubricVersion = risk.RubricVersion!, EncryptedRiskAssessment = CodeInsightRiskAssessmentProtection.Protect(risk, codec),
        };
        var legacy = new CodeInsightFinding
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), ObservedAt = date,
            EncryptedRiskAssessment = CodeInsightRiskAssessmentProtection.Protect(
                new FindingRiskAssessment(
                    risk.Impact, risk.Likelihood,
                    risk.ImpactReason, risk.LikelihoodReason, risk.Source, risk.RubricVersion), codec),
        };
        db.AddRange(retained, legacy);
        db.CodeInsightDailyCounts.Add(
            new()
            {
                Id = Guid.NewGuid(), ClientId = pr.ClientId, RepositoryId = pr.RepositoryId, PullRequestId = pr.PullRequestId,
                FilePath = string.Empty, JobId = retained.JobId, BucketDate = DateOnly.FromDateTime(date.UtcDateTime),
                Dimension = CodeInsightCountDimension.FindingTotal, Count = 1, UpdatedAt = date,
            });
        await db.SaveChangesAsync();
        var encrypted = retained.EncryptedRiskAssessment;
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(gate)
            .AddSingleton<ISecretProtectionCodec>(codec)
            .AddSingleton<ILogger<ReviewerPerformanceCountProjector>>(NullLogger<ReviewerPerformanceCountProjector>.Instance)
            .AddTransient<ReviewerPerformanceCountProjector>().BuildServiceProvider();
        var projector = services.GetRequiredService<ReviewerPerformanceCountProjector>();
        Assert.Equal(1, await projector.BackfillAsync(1));
        Assert.Equal(FindingConfidence.Certain, retained.Confidence);
        Assert.Equal(FindingConfidence.Unknown, legacy.Confidence);
        Assert.Equal(encrypted, retained.EncryptedRiskAssessment);
        Assert.Empty(await db.CodeInsightDailyCounts.ToListAsync());
        Assert.Contains(await db.ReviewerPerformanceDailyCounts.ToListAsync(), cell => cell.Confidence == FindingConfidence.Certain);
        var quality = new CodeInsightRollupProjector(db, gate, NullLogger<CodeInsightRollupProjector>.Instance);
        await quality.BackfillAsync(10);
        var scope = new CodeInsightRollupQuery([pr.ClientId], DateOnly.FromDateTime(date.UtcDateTime), DateOnly.FromDateTime(date.UtcDateTime));
        var reader = new CodeInsightRollupReader(db);
        Assert.Equal(1, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Certain }));
        Assert.Equal(1, await reader.GetTotalAsync(scope with { Confidence = FindingConfidence.Unknown }));
    }

    [Fact]
    public async Task ConfidenceSeparatesProjectionHashesAndCoalescedMeasurements()
    {
        await using var db = CreateDb();
        var date = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 1 };
        db.Add(pr);
        foreach (var confidence in new[] { FindingConfidence.Low, FindingConfidence.Certain })
        {
            db.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, JobId = Guid.NewGuid(), ObservedAt = date,
                    Confidence = confidence, Impact = FindingImpact.High, Likelihood = FindingLikelihood.Unlikely,
                    RiskSeverity = FindingRiskSeverity.Medium, RiskRubricVersion = "finding-risk-v1",
                    PublicationState = CodeInsightPublicationState.Published, ProviderCommentId = confidence.ToString(), NativeStatus = "Fixed",
                    CurrentDisposition = CodeInsightDisposition.Addressed,
                });
        }

        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var projector = new ReviewerPerformanceCountProjector(db, gate, NullLogger<ReviewerPerformanceCountProjector>.Instance);
        var measurement = (ReviewerPerformanceDailyCount row) => new
        {
            row.CellKey, row.CodeInsightPullRequestId, row.ClientId, row.RepositoryId, row.ProviderScope, row.PullRequestId,
            row.BucketDate, row.ModelId, row.LogicalModelName, row.TypeMembership, row.Qualifier, row.Outcome,
            row.PublicationState, row.DuplicateState, row.IsMiss, row.IsClassified,
            row.Impact, row.Likelihood, row.Confidence, row.RiskSeverity, row.RiskRubricVersion, row.Count,
        };
        await projector.ProjectPullRequestAsync(pr.Id);
        var initial = (await db.ReviewerPerformanceDailyCounts.OrderBy(row => row.CellKey).ToListAsync())
            .Select(measurement).ToArray();
        await projector.ProjectPullRequestAsync(pr.Id);
        var replayed = await db.ReviewerPerformanceDailyCounts.OrderBy(row => row.CellKey).ToListAsync();
        Assert.Equal(initial, replayed.Select(measurement).ToArray());
        var rows = replayed.Where(row => row.Outcome == "positive").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.CellKey).Distinct().Count());
        Assert.Equal(2, ReviewerPerformanceDimensions.Coalesce(rows).Count);
        Assert.Equal(6, pr.PerformanceProjectionVersion);
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new()
            {
                Grouping = "confidence", Views = [new() { From = DateOnly.FromDateTime(date.UtcDateTime), To = DateOnly.FromDateTime(date.UtcDateTime) }],
            }, [pr.ClientId]);
        Assert.Equal(new[] { "Certain", "Low" }, result.Views[0].Series.Select(series => series.Id).Order().ToArray());
        Assert.All(result.Views[0].Series, series => Assert.Equal(1, Assert.Single(series.Points).OverallPrecision!.Counts.Outcomes.Positive));
    }

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
        var cells = await db.ReviewerPerformanceDailyCounts.ToListAsync();
        var cell = Assert.Single(cells, row => row.Outcome == "collectionUnknown");
        Assert.Equal("review-model", Assert.Single(cells, row => row.Outcome == "collectionModelOpportunity").ModelId);
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
