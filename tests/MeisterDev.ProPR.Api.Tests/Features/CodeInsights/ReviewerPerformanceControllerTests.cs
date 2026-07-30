// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.CodeInsights.Contracts;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Api.Tests.Features.CodeInsights;

/// <summary>
///     The operator reads: whether the reviewer is right, whether humans want what it says, and what it missed.
///     Gated on tenant administration rather than client access, because they judge the tool from AI-estimated
///     evidence, and the tests that matter most here are the ones that keep a plain client user out.
/// </summary>
public sealed class ReviewerPerformanceControllerTests
{
    [Fact]
    public async Task AClientUserIsRefusedEveryRead()
    {
        // The split is an authorisation boundary, not a presentation choice. Holding client roles (even
        // administrator on one) is not the same as administering the tenant.
        var harness = new CodeInsightAudienceHarness();

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetQuality()).StatusCode);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetMisses()).StatusCode);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetFindings()).StatusCode);

        Assert.Empty(harness.RequestedMetricScopes);
        Assert.Empty(harness.RequestedBrowseScopes);
    }

    [Fact]
    public async Task ATenantAdministratorSeesTheClientsOfTheTenantsTheyAdminister()
    {
        // And only those: a tenant they are merely a member of contributes nothing.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetQuality();

        Assert.All(
            harness.RequestedMetricScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
        Assert.NotEmpty(harness.RequestedMetricScopes);
    }

    [Fact]
    public async Task ATenantAdministratorCannotNameAClientOutsideTheirTenants()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        var result = await harness.ReviewerPerformance.GetQuality(clientId: CodeInsightAudienceHarness.SomebodyElses);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        Assert.Empty(harness.RequestedMetricScopes);
    }

    [Fact]
    public async Task APlatformAdministratorSeesEveryClient()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true);

        await harness.ReviewerPerformance.GetQuality();

        Assert.All(
            harness.RequestedMetricScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB,
                CodeInsightAudienceHarness.SomebodyElses));
    }

    [Fact]
    public async Task GroupingIsRefusedToAClientUserLikeEveryOtherReadHere()
    {
        var harness = new CodeInsightAudienceHarness();

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetByGrain()).StatusCode);
        Assert.Empty(harness.RequestedMetricScopes);
    }

    [Fact]
    public async Task GroupedCorrectnessComesBackWorstFirstWithTheScopeItBelongsTo()
    {
        // A ranked list of where the reviewer is weakest is the reason to group at all.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.WithByGrain(
            ("payments-api", null, 0.82, 40),
            ("quiet-service", null, 0.38, 11),
            ("checkout-web", null, 0.64, 18));

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetByGrain());

        var rows = (IReadOnlyList<CodeInsightScopedMetricResponse>)result.Value!;
        Assert.Equal(["quiet-service", "checkout-web", "payments-api"], rows.Select(row => row.RepositoryId));
        Assert.All(rows, row => Assert.Equal("Client A", row.ClientName));
    }

    [Fact]
    public async Task AScopeWithNoComputableCorrectnessSortsLastRatherThanAsAZeroItNeverEarned()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.WithByGrain(
            ("unmeasured", null, null, 0),
            ("payments-api", null, 0.82, 40),
            ("quiet-service", null, 0.38, 11));

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetByGrain());

        var rows = (IReadOnlyList<CodeInsightScopedMetricResponse>)result.Value!;
        Assert.Equal("unmeasured", rows[^1].RepositoryId);
        Assert.Null(rows[^1].Metric.F1);
    }

    [Fact]
    public async Task GroupingByModelReportsOnlyWhatAModelCanBeHeldTo()
    {
        // Worst first on precision, and no recall or F1: a miss is a problem no finding of ours described, so
        // there is no model to charge it to.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.WithByModel(
            ("cheap-1", "thrifty", 0.61, 90),
            ("dear-1", "balanced", 0.88, 150));

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetByGrain(grain: "model"));

        var rows = (IReadOnlyList<CodeInsightScopedMetricResponse>)result.Value!;
        Assert.Equal(["thrifty", "balanced"], rows.Select(row => row.LogicalModelName));
        Assert.Equal(["cheap-1", "dear-1"], rows.Select(row => row.ModelId));
        Assert.All(rows, row => Assert.Null(row.Metric.Recall));
        Assert.All(rows, row => Assert.Null(row.Metric.F1));
        Assert.All(rows, row => Assert.Equal(0, row.Metric.Misses));

        // A model row is not a client scope: it spans every client the caller administers.
        Assert.All(rows, row => Assert.Null(row.ClientId));
        Assert.All(rows, row => Assert.Null(row.RepositoryId));

        // And it never went near the seals, which have nothing to split by model.
        Assert.Empty(harness.RequestedGrains);
    }

    [Fact]
    public async Task GroupingByModelIsRefusedToAClientUserLikeEveryOtherReadHere()
    {
        var harness = new CodeInsightAudienceHarness();

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetByGrain(grain: "model")).StatusCode);
        Assert.Empty(harness.RequestedMetricScopes);
    }

    [Fact]
    public async Task AModelRowWithNoRecordedModelComesBackAsTheUnattributedRowRatherThanBeingDropped()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.WithByModel((null, null, 0.5, 8));

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetByGrain(grain: "model"));

        var row = Assert.Single((IReadOnlyList<CodeInsightScopedMetricResponse>)result.Value!);
        Assert.Null(row.ModelId);
        Assert.Null(row.LogicalModelName);
        Assert.Equal(8, row.Metric.SampleSize);
    }

    [Fact]
    public async Task TheGrainDefaultsToRepositoryAndAnUnrecognisedOneIsNotAFailure()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetByGrain();
        await harness.ReviewerPerformance.GetByGrain(grain: "galaxy");
        await harness.ReviewerPerformance.GetByGrain(grain: "pullRequest");

        Assert.Equal(CodeInsightGrain.Repository, harness.RequestedGrains[0]);
        Assert.Equal(CodeInsightGrain.Repository, harness.RequestedGrains[1]);
        Assert.Equal(CodeInsightGrain.PullRequest, harness.RequestedGrains[2]);
    }

    [Fact]
    public async Task WithoutTheLicenceTheAreaIsDenied()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, licensed: false);

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetQuality()).StatusCode);
        Assert.Empty(harness.RequestedMetricScopes);
    }

    [Fact]
    public async Task AFailingLicenceLookupFailsClosed()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.Licensing
            .IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("licensing is unavailable"));

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetQuality()).StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        var harness = new CodeInsightAudienceHarness(authenticated: false, tenantAdmin: true);

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            ((ObjectResult)await harness.ReviewerPerformance.GetQuality()).StatusCode);
    }

    [Fact]
    public async Task WithTheSliceUnregisteredTheReadsAreEmptyRatherThanFailing()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, withReaders: false);

        var quality = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetQuality());

        Assert.Empty(((CodeInsightQualityResponse)quality.Value!).Correctness);
        Assert.Null(((CodeInsightQualityResponse)quality.Value!).CorrectnessTotal.F1);
    }

    [Fact]
    public async Task TheMinimumSampleTravelsWithTheMetricsSoNoCallerCarriesItsOwnCopy()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, minimumSampleSize: "4");

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetQuality());

        Assert.Equal(4, ((CodeInsightQualityResponse)result.Value!).MinimumSampleSize);
    }

    [Fact]
    public async Task AMisconfiguredZeroMinimumSampleIsFlooredAtOne()
    {
        // Zero would mean "present a metric computed from nothing as precise", which is the failure the threshold
        // exists to prevent.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, minimumSampleSize: "0");

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetQuality());

        Assert.Equal(1, ((CodeInsightQualityResponse)result.Value!).MinimumSampleSize);
    }

    [Fact]
    public async Task ATrendWithTooFewQualifyingBucketsHasNoDirection()
    {
        // Never an arrow through two closed pull requests, and the count comes back so a caller can say how far
        // the window is from testable.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, minimumSampleSize: "10");
        harness.WithCorrectnessSeries(
            (new DateOnly(2026, 6, 1), 0.4, 20),
            (new DateOnly(2026, 6, 8), 0.9, 2));

        var quality = Quality(await harness.ReviewerPerformance.GetQuality());

        Assert.Equal(CodeInsightTrendDirection.Insufficient, quality.CorrectnessTrend.Direction);
        // One bucket qualified; the other rested on two closed pull requests and was skipped rather than zeroed.
        Assert.Equal(1, quality.CorrectnessTrend.Periods);
        Assert.Null(quality.CorrectnessTrend.PValue);
        Assert.True(quality.MinimumTrendPeriods > 1);
    }

    [Fact]
    public async Task ARisingTrendReadsAsImprovingAndAFallingOneAsDeclining()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, minimumSampleSize: "2");
        harness.WithCorrectnessSeries(RisingWeeks(0.40, step: 0.03));

        var rising = Quality(await harness.ReviewerPerformance.GetQuality()).CorrectnessTrend;

        Assert.Equal(CodeInsightTrendDirection.Improving, rising.Direction);
        // The slope is what a reader can argue with, where a bare arrow is not: three points of F1 per week.
        Assert.Equal(0.03, rising.SlopePerPeriod!.Value, 10);
        Assert.True(rising.PValue < 0.05);
        Assert.Equal(1d, rising.Tau!.Value, 10);

        harness.WithCorrectnessSeries(RisingWeeks(0.61, step: -0.03));

        var falling = Quality(await harness.ReviewerPerformance.GetQuality()).CorrectnessTrend;

        Assert.Equal(CodeInsightTrendDirection.Declining, falling.Direction);
        Assert.Equal(-0.03, falling.SlopePerPeriod!.Value, 10);
    }

    [Fact]
    public async Task AMetricThatWanderedWithoutGoingAnywhereIsReportedAsFlat()
    {
        // Enough buckets to test, and the movement between them does not survive the test. The old reading
        // compared the first bucket against the last, which called this one improving.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, minimumSampleSize: "2");
        harness.WithCorrectnessSeries(
            (new DateOnly(2026, 6, 1), 0.60, 10),
            (new DateOnly(2026, 6, 8), 0.66, 10),
            (new DateOnly(2026, 6, 15), 0.58, 10),
            (new DateOnly(2026, 6, 22), 0.71, 10),
            (new DateOnly(2026, 6, 29), 0.55, 10),
            (new DateOnly(2026, 7, 6), 0.68, 10),
            (new DateOnly(2026, 7, 13), 0.62, 10),
            (new DateOnly(2026, 7, 20), 0.64, 10));

        var trend = Quality(await harness.ReviewerPerformance.GetQuality()).CorrectnessTrend;

        Assert.Equal(CodeInsightTrendDirection.Flat, trend.Direction);
        Assert.True(trend.PValue > 0.05, $"p-value {trend.PValue} should not clear the significance level");
        Assert.Equal(8, trend.Periods);
    }

    /// <summary>Eight consecutive weeks moving by a fixed step, which is the shortest testable window.</summary>
    private static (DateOnly Bucket, double F1, int SampleSize)[] RisingWeeks(double from, double step) =>
    [
        .. Enumerable
            .Range(0, 8)
            .Select(week => (new DateOnly(2026, 6, 1).AddDays(week * 7), from + (step * week), 10))
    ];

    [Fact]
    public async Task TheQualityViewDefaultsToWeeklyBucketsBecauseADailyF1IsMostlyEmpty()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetQuality();

        Assert.Equal(CodeInsightBucketSize.Week, harness.RequestedMetricBuckets[0]);
    }

    [Fact]
    public async Task RejectionReasonsComeBackLargestFirstWithTheUnclassifiedRemainderApart()
    {
        // Largest first, because the reason worth acting on is the one that happens most. The remainder is its
        // own number: a rejection nobody could explain is not evidence for any particular explanation.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.WithRejectionReasons(
            unclassified: 4,
            (CodeInsightRejectionReason.OutOfScope, 3),
            (CodeInsightRejectionReason.Wrong, 9),
            (CodeInsightRejectionReason.Redundant, 1));

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetRejectionReasons());

        var response = (CodeInsightRejectionReasonsResponse)result.Value!;
        Assert.Equal(["Wrong", "OutOfScope", "Redundant"], response.Reasons.Select(reason => reason.Reason));
        Assert.Equal([9, 3, 1], response.Reasons.Select(reason => reason.Count));
        Assert.Equal(4, response.Unclassified);
        Assert.Equal(17, response.Rejections);

        // The same rejections again, split by the kind of concern they raised, each class ranked the same way.
        var byClass = Assert.Single(response.ByConcernClass);
        Assert.Equal("Functional", byClass.ConcernClass);
        Assert.Equal(["Wrong", "OutOfScope", "Redundant"], byClass.Reasons.Select(reason => reason.Reason));
        Assert.Equal(17, byClass.Rejections);
    }

    [Fact]
    public async Task RejectionReasonsAreRefusedToAClientUserLikeEveryOtherReadHere()
    {
        var harness = new CodeInsightAudienceHarness();

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.ReviewerPerformance.GetRejectionReasons()).StatusCode);
        Assert.Empty(harness.RequestedMetricScopes);
    }

    [Theory]
    [InlineData("DeveloperPreference")]
    [InlineData("developerPreference")]
    [InlineData("developer_preference")]
    public async Task AFindingDrillCanNarrowToOneRejectionReason(string reason)
    {
        // What a click on a reason in the distribution means. A reason already implies its outcome, so the
        // narrowing travels on its own. The classifier names reasons in snake case and the wire carries the
        // enum name, so both have to resolve or a caller mixing them up gets silence.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetFindings(rejectionReason: reason);

        var query = Assert.Single(harness.RequestedBrowseScopes);
        Assert.Equal(CodeInsightRejectionReason.DeveloperPreference, query.RejectionReason);
        Assert.Null(query.Disposition);
    }

    [Fact]
    public async Task AnUnknownRejectionReasonNarrowsNothingRatherThanFailing()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetFindings(rejectionReason: "not-a-reason");

        Assert.Null(Assert.Single(harness.RequestedBrowseScopes).RejectionReason);
    }

    [Fact]
    public async Task MissesComeBackWithTheJudgementsThatDidNotQualifyIncluded()
    {
        // What makes the cut-off inspectable before anybody tries to calibrate it.
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        var result = Assert.IsType<OkObjectResult>(await harness.ReviewerPerformance.GetMisses());

        var rows = (IReadOnlyList<CodeInsightMissResponse>)result.Value!;
        Assert.Contains(rows, row => row.CountsAsMiss);
        Assert.Contains(rows, row => !row.CountsAsMiss && !row.IsInScope);
    }

    [Fact]
    public async Task ADrillThroughCarriesTheOutcomeNarrowingItWasAskedFor()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        await harness.ReviewerPerformance.GetFindings(disposition: "falsePositive", limit: 5000);

        var query = Assert.Single(harness.RequestedBrowseScopes);
        Assert.Equal(CodeInsightDisposition.FalsePositive, query.Disposition);
        Assert.Equal(200, query.Limit);
    }

    private static CodeInsightQualityResponse Quality(IActionResult result)
    {
        return (CodeInsightQualityResponse)((OkObjectResult)result).Value!;
    }
}
