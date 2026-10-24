// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceRiskTests
{
    [Theory]
    [InlineData("group")]
    [InlineData("empty-filter")]
    public async Task ConfidenceDimensionSupportsGroupingAndEmptySelections(string mode)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var date = new DateOnly(2026, 10, 1);
        var client = Guid.NewGuid();
        db.Add(
            new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), ClientId = client, BucketDate = date, Outcome = "positive", Count = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        var scope = JsonSerializer.Deserialize<ReviewerPerformanceViewQuery>(
            """{"from":"2026-10-01","to":"2026-10-01","confidences":[]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new()
            {
                Views = [mode == "group" ? new() { From = date, To = date } : scope],
                Grouping = mode == "group" ? "confidence" : "none",
            }, [client]);

        if (mode == "group")
        {
            Assert.Equal("Unassessed", Assert.Single(result.Views[0].Series).Label);
        }
        else
        {
            Assert.All(result.Views[0].Series.SelectMany(series => series.Points), point => Assert.Equal(0, point.OverallPrecision!.Counts.Generated));
        }
    }

    [Fact]
    public async Task RiskFacetLabelsDistinguishUnassessedAndNotApplicableWithoutChangingIds()
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 7 };
        db.Add(pr);
        foreach (var likelihood in new[] { FindingLikelihood.Unknown, FindingLikelihood.NotApplicable })
        {
            db.Add(
                new ReviewerPerformanceDailyCount
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = pr.ClientId,
                    RepositoryId = pr.RepositoryId, PullRequestId = 7, BucketDate = new DateOnly(2026, 10, 1),
                    Outcome = "positive", Likelihood = likelihood, Count = 1, UpdatedAt = DateTimeOffset.UtcNow,
                });
        }

        await db.SaveChangesAsync();

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new()
            {
                Views = [new() { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 1) }],
                Grouping = "likelihood",
            }, [pr.ClientId]);

        var view = Assert.Single(result.Views);
        var likelihoods = Assert.IsAssignableFrom<IReadOnlyList<ReviewerPerformanceFacet>>(view.Facets.Likelihoods);
        Assert.Contains(likelihoods, item => item.Id == "Unknown" && item.Label == "Unassessed");
        Assert.Contains(likelihoods, item => item.Id == "NotApplicable" && item.Label == "Not applicable");
        Assert.Contains(view.Series, item => item.Id == "Unknown" && item.Label == "Unassessed");
        Assert.Contains(view.Series, item => item.Id == "NotApplicable" && item.Label == "Not applicable");
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("group")]
    [InlineData("matrix")]
    [InlineData("confidence-filter")]
    [InlineData("confidence-group")]
    [InlineData("confidence-matrix")]
    public async Task RiskScopesKeepPrecisionAndDisableRecallAcrossEveryMeasurement(string mode)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var date = new DateOnly(2026, 10, 1);
        var pr = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), RepositoryId = "repo", PullRequestId = 7 };
        db.Add(pr);

        ReviewerPerformanceDailyCount Cell(string outcome) => new()
        {
            Id = Guid.NewGuid(), CodeInsightPullRequestId = pr.Id, ClientId = pr.ClientId, RepositoryId = pr.RepositoryId,
            ProviderScope = "AzureDevOps:https://dev.azure.com/test", PullRequestId = 7, BucketDate = date, Outcome = outcome,
            Count = 1, UpdatedAt = DateTimeOffset.UtcNow,
        };

        var finding = Cell("positive");
        finding.PublicationState = CodeInsightPublicationState.Published;
        finding.Impact = FindingImpact.Critical;
        finding.Likelihood = FindingLikelihood.Likely;
        finding.RiskSeverity = FindingRiskSeverity.Critical;
        finding.RiskRubricVersion = "finding-risk-v1";
        finding.Confidence = FindingConfidence.Certain;
        db.AddRange(finding, Cell("collectionComplete"));
        await db.SaveChangesAsync();
        var scope = new ReviewerPerformanceViewQuery { From = date, To = date };
        if (mode == "filter")
        {
            scope = scope with { Impacts = ["Critical"] };
        }

        if (mode == "confidence-filter")
        {
            scope = scope with { Confidences = ["Certain"] };
        }

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            new()
            {
                Views = [scope],
                Grouping = mode.EndsWith("group", StringComparison.Ordinal)
                    ? mode.StartsWith("confidence", StringComparison.Ordinal) ? "confidence" : "impact"
                    : "none",
                Breakdown = mode.EndsWith("matrix", StringComparison.Ordinal)
                    ? new(mode.StartsWith("confidence", StringComparison.Ordinal) ? "confidence" : "impact", "riskSeverity")
                    : null,
            }, [pr.ClientId]);
        var measurements =
            (mode.EndsWith("matrix", StringComparison.Ordinal)
                ? result.Views[0].Breakdown.Select(cell => cell.Measurement)
                : result.Views[0].Series.SelectMany(series => series.Points))
            .ToList();
        Assert.NotEmpty(measurements);
        Assert.All(
            measurements, point =>
            {
                Assert.NotNull(point.OverallPrecision!.Summary);
                Assert.Null(point.Score.Summary.Recall);
                Assert.Null(point.Score.Summary.F1);
                Assert.False(point.Coverage!.RecallSupported);
                Assert.Contains("miss-risk-assessment-unavailable", point.UnavailableReasons);
            });
    }

    [Fact]
    public void UnselectedRiskPropertiesDoNotChangeLegacyRequestSerialization()
    {
        var scope = new ReviewerPerformanceViewQuery();
        var json = JsonSerializer.Serialize(scope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("impacts", json);
        Assert.DoesNotContain("likelihoods", json);
        Assert.DoesNotContain("riskSeverities", json);
        Assert.DoesNotContain("confidences", json);
    }
}
