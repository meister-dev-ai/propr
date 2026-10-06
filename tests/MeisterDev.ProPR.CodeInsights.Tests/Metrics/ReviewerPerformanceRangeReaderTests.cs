// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceRangeReaderTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task FreshnessIncludesDuplicateVerificationAwaitingReprojection(int secondsAfterProjection, int expectedPending)
    {
        await using var db = Db();
        var projectedAt = DateTimeOffset.UtcNow;
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = Client, RepositoryId = "repo", PullRequestId = 1,
            PerformanceProjectionVersion = ReviewerPerformanceCountProjector.ProjectionVersion,
            PerformanceProjectedAt = projectedAt
        };
        db.Add(aggregate);
        db.Add(
            new CodeInsightFinding
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, CreatedAt = projectedAt.AddSeconds(-2),
                DuplicateState = CodeInsightDuplicateState.ConfirmedDuplicate,
                DuplicateVerifiedAt = projectedAt.AddSeconds(secondsAfterProjection)
            });
        await db.SaveChangesAsync();

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client]);

        Assert.Equal(expectedPending, result.Views[0].Evidence.PendingSourceAggregates);
    }

    [Fact]
    public async Task TypeUnionCountsEachCellOnceAndPeriodCountsConserveCumulativeInputs()
    {
        await using var db = Db();
        db.ReviewerPerformanceDailyCounts.AddRange(Cell(1, 3), Cell(2, 7));
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var query = Query() with { Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 2), Types = ["logic-error", "concurrency"] }] };
        var cumulative = await reader.QueryAsync(query, [Client]);
        var period = await reader.QueryAsync(query with { Aggregation = "period" }, [Client]);
        Assert.Equal(10, cumulative.Views[0].Series[0].Points[^1].Score.Counts.Outcomes.Positive);
        Assert.Equal(10, period.Views[0].Series[0].Points.Sum(point => point.Score.Counts.Outcomes.Positive));
        Assert.Equal(7, period.Views[0].Series[0].Points[^1].Score.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task UnknownMissClassificationCannotDisappearFromFilteredRecallCoverage()
    {
        await using var db = Db();
        db.ReviewerPerformanceDailyCounts.Add(Cell(1, 8));
        var miss = Cell(1, 2);
        miss.IsMiss = true;
        miss.Outcome = "missActed";
        miss.TypeMembership = "";
        miss.IsClassified = false;
        db.ReviewerPerformanceDailyCounts.Add(miss);
        await db.SaveChangesAsync();
        var query = Query() with { Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 2), Types = ["concurrency"] }] };
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [Client]);
        var point = result.Views[0].Series[0].Points[0];
        Assert.NotNull(point.Score.Summary.Precision);
        Assert.Null(point.Score.Summary.F1);
        Assert.Contains("miss-type-attribution-unavailable", point.UnavailableReasons);
    }

    [Fact]
    public async Task ExplicitEmptySelectionMeansNoneAndUnauthorizedClientIsRefused()
    {
        await using var db = Db();
        db.ReviewerPerformanceDailyCounts.Add(Cell(1, 3));
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var empty = Query() with { Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 2), ClientIds = [] }] };
        Assert.All((await reader.QueryAsync(empty, [Client])).Views[0].Series[0].Points, point => Assert.Null(point.Score.Summary.Precision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.QueryAsync(
            empty with { Views = [empty.Views[0] with { ClientIds = [Guid.NewGuid()] }] }, [Client]));
    }

    [Fact]
    public async Task IdenticalComparedScopesHaveTheSameScenarioTuplesAndOneEvidenceCutoff()
    {
        await using var db = Db();
        db.ReviewerPerformanceDailyCounts.Add(Cell(1, 3));
        await db.SaveChangesAsync();
        var query = Query();
        query = query with { Views = [query.Views[0], query.Views[0]] };
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [Client]);
        Assert.Equal(result.Views[0].Series[0].Points[0].Score.Scenarios, result.Views[1].Series[0].Points[0].Score.Scenarios);
        Assert.Equal(162, result.PremiseIds.Count);
    }

    [Fact]
    public async Task OversizedWindowsAndUnsupportedDimensionsAreRefused()
    {
        await using var db = Db();
        var reader = new ReviewerPerformanceRangeReader(db);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryAsync(
            Query() with { Views = [new() { From = new(2020, 1, 1), To = new(2026, 1, 1) }] }, [Client]));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryAsync(Query() with { Grouping = "invented" }, [Client]));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryAsync(Query() with { Views = [null!] }, [Client]));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryAsync(
            Query() with { Views = [new() { From = DateOnly.MaxValue, To = DateOnly.MaxValue }] }, [Client]));
    }

    [Fact]
    public async Task InactivePeriodSeriesKeepsCalendarBucketsWithoutContinuingItsMeasuredBand()
    {
        await using var db = Db();
        var early = Cell(1, 8);
        early.ModelId = "early";
        var later = Cell(2, 5);
        later.ModelId = "later";
        db.AddRange(early, later);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query() with { Grouping = "model", Aggregation = "period" }, [Client]);
        var series = result.Views[0].Series.Single(row => row.Label == "early");
        Assert.NotNull(series.Points[0].Score.Summary.Precision);
        Assert.Null(series.Points[1].Score.Summary.Precision);
        Assert.Equal(0, series.Points[1].Score.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task SelectedPeriodMatrixUsesTheSameWindowAndCountsAsItsTimelinePoint()
    {
        await using var db = Db();
        db.AddRange(Cell(1, 3), Cell(2, 7));
        await db.SaveChangesAsync();
        var query = Query() with { Aggregation = "period", Breakdown = new("type", "qualifier", new(2026, 9, 1)) };
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [Client]);
        Assert.All(result.Views[0].Breakdown, cell => Assert.Equal(3, cell.Measurement.Score.Counts.Outcomes.Positive));
        Assert.All(result.Views[0].Breakdown, cell => Assert.Equal(result.Views[0].Series[0].Points[0].WindowTo, cell.Measurement.WindowTo));
        var unclassified = Cell(1, 2);
        unclassified.TypeMembership = "";
        db.Add(unclassified);
        await db.SaveChangesAsync();
        var filtered = Query() with { Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 2), Types = ["unclassified"] }] };
        Assert.Equal(
            2, (await new ReviewerPerformanceRangeReader(db).QueryAsync(filtered, [Client])).Views[0].Series[0].Points[0].Score.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task RepositoryMatrixRetainsUnknownProviderMissCoverage()
    {
        await using var db = Db();
        var finding = Cell(1, 8);
        finding.ProviderScope = "GitHub:https://github.example";
        var coverage = Cell(1, 1);
        coverage.ProviderScope = finding.ProviderScope;
        coverage.Outcome = "collectionComplete";
        var miss = Cell(1, 2);
        miss.IsMiss = true;
        miss.Outcome = "missActed";
        db.AddRange(finding, coverage, miss);
        await db.SaveChangesAsync();
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(Query() with { Breakdown = new("repository", "type", new(2026, 9, 1)) }, [Client]);
        Assert.All(result.Views[0].Breakdown, cell => Assert.Contains("miss-provider-scope-unavailable", cell.Measurement.UnavailableReasons));
        Assert.All(result.Views[0].Breakdown, cell => Assert.Null(cell.Measurement.Score.Summary.F1));
    }

    [Theory]
    [InlineData("canonical", 4, 0, true)]
    [InlineData("unknown-scope", 2, 7, true)]
    [InlineData("base64-whitespace", 0, 0, true)]
    [InlineData("json-whitespace", 0, 0, true)]
    [InlineData("null-source", 0, 0, true)]
    [InlineData("invalid-utf8-source", 0, 0, true)]
    [InlineData("noncanonical-client", 0, 0, false)]
    [InlineData("different-repository-case", 0, 0, false)]
    [InlineData("malformed", 0, 0, false)]
    [InlineData("duplicates", 4, 0, true)]
    public async Task RepositorySelectorsPreserveExactKeysAndUnknownScopeFallback(
        string selectionKind, int expectedPositive, int expectedMisses, bool expectedUnknownScope)
    {
        await using var db = Db();
        var finding = Cell(1, 4);
        finding.ProviderScope = "GitHub:https://github.example";
        var otherSourceFinding = Cell(1, 8);
        otherSourceFinding.ProviderScope = "GitLab:https://gitlab.example";
        var unknownSourceFinding = Cell(1, 2);
        var unknownSourceMiss = Cell(1, 7);
        unknownSourceMiss.IsMiss = true;
        unknownSourceMiss.Outcome = "missActed";
        db.AddRange(finding, otherSourceFinding, unknownSourceFinding, unknownSourceMiss);
        await db.SaveChangesAsync();
        var canonical = ReviewerPerformanceRangeReader.RepositoryKey(finding);
        var selectors = selectionKind switch
        {
            "canonical" => new[] { canonical },
            "unknown-scope" => [ReviewerPerformanceRangeReader.RepositoryKey(unknownSourceFinding)],
            "base64-whitespace" => [$" {canonical[..4]}\r\n{canonical[4..]} "],
            "json-whitespace" => [EncodeRepositorySelector($"[ \"{Client}\", \"{finding.ProviderScope}\", \"repo\" ]")],
            "null-source" => [EncodeRepositorySelector(JsonSerializer.Serialize(new string?[] { Client.ToString(), null, "repo" }))],
            "invalid-utf8-source" =>
            [
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"[\"{Client}\",\"").Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes("\",\"repo\"]")).ToArray())
            ],
            "noncanonical-client" => [EncodeRepositorySelector(JsonSerializer.Serialize(new[] { $"{{{Client}}}", finding.ProviderScope, "repo" }))],
            "different-repository-case" => [EncodeRepositorySelector(JsonSerializer.Serialize(new[] { Client.ToString(), finding.ProviderScope, "Repo" }))],
            "malformed" =>
            [
                "!invalid", EncodeRepositorySelector("[invalid"), EncodeRepositorySelector("null"),
                EncodeRepositorySelector(JsonSerializer.Serialize(new[] { Client.ToString(), "repo" })),
                EncodeRepositorySelector(JsonSerializer.Serialize(new[] { Client.ToString(), finding.ProviderScope, "repo", "extra" }))
            ],
            "duplicates" => [canonical, "!invalid", canonical],
            _ => throw new ArgumentException("Unsupported selection kind.", nameof(selectionKind))
        };
        var query = Query() with { Views = [Query().Views[0] with { Repositories = selectors }] };

        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(query, [Client]);

        var point = Assert.Single(result.Views[0].Series).Points[0];
        Assert.Equal(expectedPositive, point.Score.Counts.Outcomes.Positive);
        Assert.Equal(expectedMisses, point.Score.Counts.ActedMisses);
        Assert.Equal(expectedUnknownScope, point.UnavailableReasons.Contains("miss-provider-scope-unavailable"));
    }

    [Fact]
    public async Task CumulativeTimelineAndMatrixRetainPriorEvidenceWithoutNewBucketActivity()
    {
        await using var db = Db();
        db.Add(Cell(1, 8));
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var query = Query() with { Breakdown = new("type", "qualifier", new(2026, 9, 2)) };
        var result = await reader.QueryAsync(query, [Client]);
        Assert.NotEmpty(result.Views[0].Breakdown);
        Assert.NotNull(result.Views[0].Series[0].Points[1].Score.Summary.Precision);
        Assert.All(result.Views[0].Breakdown, cell => Assert.NotNull(cell.Measurement.Score.Summary.Precision));
        Assert.All(result.Views[0].Breakdown, cell => Assert.Equal(8, cell.Measurement.Score.Counts.Outcomes.Positive));
        var period = await reader.QueryAsync(query with { Aggregation = "period" }, [Client]);
        Assert.Null(period.Views[0].Series[0].Points[1].Score.Summary.Precision);
        Assert.Empty(period.Views[0].Breakdown);
    }

    [Fact]
    public async Task CumulativeModelRetainsEarlierEvidenceAcrossInactiveBucketsWhilePeriodHasGaps()
    {
        await using var db = Db();
        var first = Cell(1, 3);
        first.ModelId = "review";
        var third = Cell(3, 7);
        third.ModelId = "review";
        db.AddRange(first, third);
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var query = Query() with { Grouping = "model", Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 4) }] };
        var result = await reader.QueryAsync(query, [Client]);
        var points = Assert.Single(result.Views[0].Series).Points;
        Assert.NotNull(points[1].Score.Summary.Precision);
        Assert.Equal(3, points[1].Score.Counts.Outcomes.Positive);
        Assert.NotNull(points[3].Score.Summary.Precision);
        Assert.Equal(10, points[3].Score.Counts.Outcomes.Positive);
        var period = (await reader.QueryAsync(query with { Aggregation = "period" }, [Client])).Views[0].Series[0].Points;
        Assert.Null(period[1].Score.Summary.Precision);
        Assert.Null(period[3].Score.Summary.Precision);
    }

    [Fact]
    public async Task GlobalRecallWithholdsUnknownSourceMissesThatCannotBeReconciledWithScopedReplay()
    {
        await using var db = Db();
        var finding = Cell(1, 8);
        finding.ProviderScope = "GitHub:https://github.example";
        var coverage = Cell(1, 1);
        coverage.ProviderScope = finding.ProviderScope;
        coverage.Outcome = "collectionComplete";
        var legacyMiss = Cell(1, 1);
        legacyMiss.IsMiss = true;
        legacyMiss.Outcome = "missActed";
        var scopedMiss = Cell(1, 1);
        scopedMiss.IsMiss = true;
        scopedMiss.Outcome = "missActed";
        scopedMiss.ProviderScope = finding.ProviderScope;
        db.AddRange(finding, coverage, legacyMiss, scopedMiss);
        await db.SaveChangesAsync();
        var point = (await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client])).Views[0].Series[0].Points[0];
        Assert.NotNull(point.Score.Summary.Precision);
        Assert.Null(point.Score.Summary.Recall);
        Assert.Null(point.Score.Summary.F1);
        Assert.Equal(2, point.Score.Counts.ActedMisses);
        Assert.Contains("miss-provider-scope-unavailable", point.UnavailableReasons);
    }

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    public async Task PartialBucketTimelineKeysCanBeExploredWithTheSameClippedWindow(string bucket)
    {
        await using var db = Db();
        db.Add(Cell(9, 8));
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var query = Query() with { Bucket = bucket, Aggregation = "period", Views = [new() { From = new(2026, 9, 9), To = new(2026, 9, 11) }] };
        var point = (await reader.QueryAsync(query, [Client])).Views[0].Series[0].Points[0];
        var explored = await reader.QueryAsync(query with { Breakdown = new("type", "qualifier", point.Date) }, [Client]);
        Assert.All(
            explored.Views[0].Breakdown, cell =>
            {
                Assert.Equal(point.WindowFrom, cell.Measurement.WindowFrom);
                Assert.Equal(point.WindowTo, cell.Measurement.WindowTo);
                Assert.Equal(point.Score.Counts, cell.Measurement.Score.Counts);
            });
    }

    [Fact]
    public async Task UnsupportedMissPopulationHasNoScenarioFalseNegativeVerdict()
    {
        await using var db = Db();
        db.Add(Cell(1, 8));
        await db.SaveChangesAsync();
        var point = (await new ReviewerPerformanceRangeReader(db).QueryAsync(Query(), [Client])).Views[0].Series[0].Points[0];
        Assert.Equal(0, point.Score.Counts.ActedMisses);
        Assert.All(
            point.Score.Scenarios, scenario =>
            {
                Assert.NotNull(scenario.Precision);
                Assert.Null(scenario.FalseNegatives);
            });
    }

    [Theory]
    [InlineData("client")]
    [InlineData("repository")]
    [InlineData("type")]
    [InlineData("qualifier")]
    public async Task GroundedMissOnlyPopulationsAppearInGroupedTimelineAndMatrix(string dimension)
    {
        await using var db = Db();
        var miss = Cell(1, 2);
        miss.IsMiss = true;
        miss.Outcome = "missActed";
        miss.ProviderScope = "GitHub:https://github.example";
        miss.TypeMembership = "concurrency";
        miss.Qualifier = "Missing";
        var coverage = Cell(1, 1);
        coverage.ProviderScope = miss.ProviderScope;
        coverage.Outcome = "collectionComplete";
        db.AddRange(miss, coverage);
        await db.SaveChangesAsync();
        var other = dimension == "qualifier" ? "type" : "qualifier";
        var result = await new ReviewerPerformanceRangeReader(db).QueryAsync(
            Query() with { Grouping = dimension, Breakdown = new(dimension, other, new(2026, 9, 1)) }, [Client]);
        var point = Assert.Single(result.Views[0].Series).Points[0];
        Assert.Equal(2, point.Score.Counts.ActedMisses);
        Assert.Equal(0, point.Score.Summary.Recall!.Median);
        Assert.Equal(0, point.Score.Summary.F1!.Median);
        Assert.NotEmpty(result.Views[0].Breakdown);
        Assert.All(result.Views[0].Breakdown, cell => Assert.Equal(0, cell.Measurement.Score.Summary.F1!.Median));
    }

    [Fact]
    public async Task MaximumUnicodeIdentitiesEmittedByFacetsCanBeSelectedAndExplored()
    {
        await using var db = Db();
        var finding = Cell(1, 3);
        finding.RepositoryId = new string('界', 512);
        finding.ProviderScope = new string('界', 1024);
        finding.ModelId = new string('界', 256);
        finding.LogicalModelName = new string('界', 128);
        db.Add(finding);
        await db.SaveChangesAsync();
        var reader = new ReviewerPerformanceRangeReader(db);
        var facets = (await reader.QueryAsync(Query(), [Client])).Views[0].Facets;
        var scope = Query().Views[0] with { Repositories = [facets.Repositories[0].Id], Models = [facets.Models[0].Id] };
        var result = await reader.QueryAsync(Query() with { Views = [scope], Breakdown = new("repository", "model", new(2026, 9, 1)) }, [Client]);
        Assert.Equal(3, result.Views[0].Series[0].Points[0].Score.Counts.Outcomes.Positive);
        Assert.Single(result.Views[0].Breakdown);
    }

    private static ReviewerPerformanceQuery Query() => new() { Bucket = "day", Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 2) }] };

    private static string EncodeRepositorySelector(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReviewerPerformanceDailyCount Cell(int day, int count) => new()
    {
        Id = Guid.NewGuid(), ClientId = Client, CodeInsightPullRequestId = Guid.NewGuid(), RepositoryId = "repo",
        BucketDate = new(2026, 9, day), PublicationState = CodeInsightPublicationState.Published,
        Outcome = "positive", TypeMembership = "concurrency|logic-error", IsClassified = true, Count = count,
    };
}
