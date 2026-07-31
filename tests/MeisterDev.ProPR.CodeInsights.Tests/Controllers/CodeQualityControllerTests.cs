// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Http;

namespace MeisterDev.ProPR.CodeInsights.Tests.Controllers;

/// <summary>
///     The developer-facing reads: what a codebase keeps getting wrong and where. Client access plus the licence,
///     and (above everything else) exactly the clients the caller holds a role on.
/// </summary>
public sealed class CodeQualityControllerTests
{
    [Fact]
    public async Task EveryReadIsScopedToExactlyTheClientsTheCallerHolds()
    {
        // The highest-cost defect in the feature, so all three reads are checked rather than one representative.
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetTypesOverTime();
        await harness.CodeQuality.GetConcentration();
        await harness.CodeQuality.GetFindings();

        Assert.NotEmpty(harness.RequestedRollupScopes);
        Assert.NotEmpty(harness.RequestedBrowseScopes);
        Assert.All(
            harness.RequestedRollupScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
        Assert.All(
            harness.RequestedBrowseScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
    }

    [Fact]
    public async Task EveryReadCanBeNarrowedToOnePullRequest()
    {
        // What the tab embedded in a review rests on. Without the filter reaching all three reads, that tab would
        // show the whole client's numbers under a heading naming one pull request.
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetTypesOverTime(pullRequestId: 4821);
        await harness.CodeQuality.GetConcentration(pullRequestId: 4821);
        await harness.CodeQuality.GetSurvival(pullRequestId: 4821);

        // The type series asks its reader twice (series and total), and survival reads through its own reader.
        Assert.All(harness.RequestedRollupScopes, scope => Assert.Equal(4821, scope.PullRequestId));
        Assert.All(harness.RequestedSurvivalScopes, scope => Assert.Equal(4821, scope.PullRequestId));
        Assert.NotEmpty(harness.RequestedSurvivalScopes);
        Assert.All(
            harness.RequestedRollupScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
    }

    [Fact]
    public async Task TheHotspotReadPassesTheFileSelectorSeparatelyFromTheScope()
    {
        // The parameter chooses which files to report on, never which findings to count: the whole reason the
        // view embedded in a review can say "this file has produced thirty findings before today".
        var harness = new CodeInsightAudienceHarness();
        harness.WithHotspots(
            new CodeInsightHotspotReport(
                30,
                12,
                2.5,
                2,
                [new CodeInsightFileHotspot("src/Service.cs", 21, 9, 21d / 9d)]));

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetHotspots(repositoryId: "repo-1", filesFromPullRequestId: 4821));

        Assert.Equal(CodeInsightHotspotGrouping.File, Assert.Single(harness.RequestedHotspotGroupings));

        var report = (CodeInsightHotspotResponse)result.Value!;
        Assert.Equal(30, report.TotalFindings);
        Assert.Equal(2, report.FileCount);
        Assert.Equal(21, Assert.Single(report.Files).Findings);

        Assert.Equal(4821, Assert.Single(harness.RequestedHotspotFileSelectors));
        // The scope it read in carries no pull request of its own.
        Assert.All(harness.RequestedRollupScopes, scope => Assert.Null(scope.PullRequestId));
        Assert.All(
            harness.RequestedRollupScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
    }

    [Fact]
    public async Task TheHotspotReadCanGroupByDefinitionAndCarriesWhatItCouldNotPlace()
    {
        var harness = new CodeInsightAudienceHarness();
        harness.WithHotspots(
            new CodeInsightHotspotReport(
                27,
                9,
                3,
                2,
                [new CodeInsightFileHotspot("src/Service.cs", 18, 8, 2.25, "Process")],
                UnplacedFindings: 13));

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetHotspots(groupBy: "symbol"));

        var report = (CodeInsightHotspotResponse)result.Value!;
        Assert.Equal("Process", Assert.Single(report.Files).SymbolName);
        // The findings the syntax could not place travel with the report rather than being ranked as a bucket.
        Assert.Equal(13, report.UnplacedFindings);
        Assert.Equal(CodeInsightHotspotGrouping.Symbol, Assert.Single(harness.RequestedHotspotGroupings));
    }

    [Fact]
    public async Task AnUnrecognisedHotspotGroupingFallsBackToFilesRatherThanFailingTheRead()
    {
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetHotspots(groupBy: "galaxy");

        Assert.Equal(CodeInsightHotspotGrouping.File, Assert.Single(harness.RequestedHotspotGroupings));
    }

    [Fact]
    public async Task TheHotspotReadIsRefusedWithoutTheLicence()
    {
        var harness = new CodeInsightAudienceHarness(licensed: false);

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.CodeQuality.GetHotspots()).StatusCode);
        Assert.Empty(harness.RequestedRollupScopes);
    }

    [Fact]
    public async Task TheSurvivalReadIsScopedAndReportsBothTotalsAndTheWorstPullRequests()
    {
        var harness = new CodeInsightAudienceHarness();

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetSurvival());

        var report = (CodeInsightSurvivalReport)result.Value!;
        Assert.Equal(9, report.Total.Persisted);
        Assert.Equal(3, report.Total.Fixed);
        Assert.Equal(2, report.Total.Dropped);
        Assert.Equal(14, report.Total.Total);
        Assert.Equal(9d / 14d, report.Total.PersistenceRate!.Value, 12);
        Assert.Equal(4790, Assert.Single(report.PullRequests).PullRequestId);
        Assert.All(
            harness.RequestedSurvivalScopes,
            scope => CodeInsightAudienceHarness.AssertExactly(
                scope,
                CodeInsightAudienceHarness.MineA,
                CodeInsightAudienceHarness.MineB));
    }

    [Fact]
    public async Task WithNothingMeasuredTheSurvivalRateIsUndefinedRatherThanZero()
    {
        // Nothing measured is not the same as nothing persisting, and a chart would draw the difference.
        var harness = new CodeInsightAudienceHarness(withReaders: false);

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetSurvival());

        var report = (CodeInsightSurvivalReport)result.Value!;
        Assert.Null(report.Total.PersistenceRate);
        Assert.Equal(0, report.Total.PullRequests);
        Assert.Empty(report.PullRequests);
    }

    [Fact]
    public async Task AskingForAClientTheCallerCannotSeeIsDeniedRatherThanEmptied()
    {
        // An empty result would hide an authorisation failure behind what looks like missing data.
        var harness = new CodeInsightAudienceHarness();

        var result = await harness.CodeQuality.GetTypesOverTime(clientId: CodeInsightAudienceHarness.SomebodyElses);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        Assert.Empty(harness.RequestedRollupScopes);
    }

    [Fact]
    public async Task AskingForAClientTheCallerCanSeeNarrowsToIt()
    {
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetTypesOverTime(clientId: CodeInsightAudienceHarness.MineB);

        CodeInsightAudienceHarness.AssertExactly(
            Assert.Single(harness.RequestedRollupScopes),
            CodeInsightAudienceHarness.MineB);
    }

    [Fact]
    public async Task AnAdministratorAggregatesOverEveryClientWithoutNamingThem()
    {
        var harness = new CodeInsightAudienceHarness(isAdmin: true);

        await harness.CodeQuality.GetConcentration();

        CodeInsightAudienceHarness.AssertExactly(
            Assert.Single(harness.RequestedRollupScopes),
            CodeInsightAudienceHarness.MineA,
            CodeInsightAudienceHarness.MineB,
            CodeInsightAudienceHarness.SomebodyElses);
    }

    [Fact]
    public async Task WithoutTheLicenceTheAreaIsDenied()
    {
        // A commercial area, not a side-read that degrades: a deep link must not succeed because a frontend flag
        // was flipped.
        var harness = new CodeInsightAudienceHarness(licensed: false);

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.CodeQuality.GetTypesOverTime()).StatusCode);
        Assert.Empty(harness.RequestedRollupScopes);
    }

    [Fact]
    public async Task WithNoLicensingServiceRegisteredTheAreaFailsClosed()
    {
        var harness = new CodeInsightAudienceHarness(withLicensingService: false);

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.CodeQuality.GetTypesOverTime()).StatusCode);
    }

    [Fact]
    public async Task AFailingLicenceLookupFailsClosed()
    {
        var harness = new CodeInsightAudienceHarness();
        harness.Licensing
            .IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("licensing is unavailable"));

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.CodeQuality.GetTypesOverTime()).StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        var harness = new CodeInsightAudienceHarness(authenticated: false);

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            ((ObjectResult)await harness.CodeQuality.GetTypesOverTime()).StatusCode);
    }

    [Fact]
    public async Task ACallerWithNoClientAccessAtAllIsRefused()
    {
        var harness = new CodeInsightAudienceHarness(withClientRoles: false);

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            ((ObjectResult)await harness.CodeQuality.GetTypesOverTime()).StatusCode);
    }

    [Fact]
    public async Task WithTheSliceUnregisteredTheReadsAreEmptyRatherThanFailing()
    {
        // Licensed but not registered: a database-less installation. "Nothing collected" is a state the view
        // renders anyway.
        var harness = new CodeInsightAudienceHarness(withReaders: false);

        var types = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetTypesOverTime());

        Assert.Empty(((CodeInsightTypeSeriesResponse)types.Value!).Points);
    }

    [Fact]
    public async Task TheDefaultWindowIsTheLastThirtyDaysAndAReversedRangeIsRepaired()
    {
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetTypesOverTime();
        var defaulted = harness.RequestedRollupScopes[0];

        await harness.CodeQuality.GetTypesOverTime(
            from: new DateOnly(2026, 6, 30),
            to: new DateOnly(2026, 6, 1));
        var reversed = harness.RequestedRollupScopes[1];

        Assert.Equal(30, defaulted.To.DayNumber - defaulted.From.DayNumber);
        // A reversed range is read as the window the caller meant rather than as an empty one.
        Assert.Equal(new DateOnly(2026, 6, 1), reversed.From);
        Assert.Equal(new DateOnly(2026, 6, 30), reversed.To);
    }

    [Fact]
    public async Task TheConcentrationRankingCarriesClientNamesSoItIsActionable()
    {
        var harness = new CodeInsightAudienceHarness();
        harness.WithConcentration(new CodeInsightConcentrationRow(CodeInsightAudienceHarness.MineA, "busy-repo", null, null, null, 7));

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetConcentration());

        var row = Assert.Single((IReadOnlyList<CodeInsightConcentrationResponse>)result.Value!);
        Assert.Equal("Client A", row.ClientName);
        Assert.Equal("busy-repo", row.RepositoryId);
        Assert.Equal(7, row.Count);
    }

    [Fact]
    public async Task AFailingClientLookupStillReturnsTheRanking()
    {
        // A ranking without display names is less useful; a ranking that failed to load is useless.
        var harness = new CodeInsightAudienceHarness();
        harness.WithConcentration(new CodeInsightConcentrationRow(CodeInsightAudienceHarness.MineA, "busy-repo", null, null, null, 7));
        harness.Clients
            .GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the client directory is unavailable"));

        var result = Assert.IsType<OkObjectResult>(await harness.CodeQuality.GetConcentration());

        Assert.Null(Assert.Single((IReadOnlyList<CodeInsightConcentrationResponse>)result.Value!).ClientName);
    }

    [Fact]
    public async Task ADrillThroughCarriesTheTypeNarrowingAndClampsItsLimit()
    {
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetFindings(coreType: "concurrency", limit: 5000);

        var query = Assert.Single(harness.RequestedBrowseScopes);
        Assert.Equal("concurrency", query.CoreType);
        // A drill-through is a sample somebody is about to read, not an export.
        Assert.Equal(200, query.Limit);
    }

    [Fact]
    public async Task AnUnrecognisedNarrowingIsIgnoredRatherThanFailingTheRead()
    {
        var harness = new CodeInsightAudienceHarness();

        await harness.CodeQuality.GetTypesOverTime(bucket: "fortnight");
        await harness.CodeQuality.GetConcentration(grain: "galaxy");

        Assert.Equal(CodeInsightBucketSize.Day, harness.RequestedBuckets[0]);
        Assert.Equal(CodeInsightGrain.Repository, harness.RequestedGrains[0]);
    }
}
