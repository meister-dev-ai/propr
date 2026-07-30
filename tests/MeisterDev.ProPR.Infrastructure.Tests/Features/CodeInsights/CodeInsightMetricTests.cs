// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

/// <summary>
///     Sealing the correctness measurement at close, and reading both lenses back. What these tests protect is
///     the handful of properties that make the numbers trustworthy: a seal happens once, aggregation sums the
///     counts rather than averaging the ratios, and undefined never quietly becomes zero.
/// </summary>
public sealed class CodeInsightMetricTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ClientB = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset ReviewedAt = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;
    private readonly CodeInsightRollupProjector _projector;
    private readonly CodeInsightMetricSealer _sealer;
    private readonly CodeInsightMetricReader _reader;
    private readonly ICodeInsightsCollectionGate _gate;

    public CodeInsightMetricTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightMetricTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());

        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this._projector = new CodeInsightRollupProjector(
            this._dbContext,
            this._gate,
            NullLogger<CodeInsightRollupProjector>.Instance);
        this._sealer = new CodeInsightMetricSealer(
            this._dbContext,
            this._gate,
            NullLogger<CodeInsightMetricSealer>.Instance);
        this._reader = new CodeInsightMetricReader(
            this._dbContext,
            new CodeInsightRollupReader(this._dbContext));
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Abandoned")]
    [InlineData("Closed")]
    public async Task EveryCloseTypeSealsTheSameMeasurement(string closeState)
    {
        // A finding the reviewer got right was right whether or not the pull request was merged. The close type
        // is recorded, not applied.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 1, dismissed: 1, falsePositive: 1);
        await this.SeedMissesAsync(key, qualifying: 2, disqualified: 0);

        Assert.True(await this._sealer.SealAsync(key, closeState));

        var seal = await this.LoadSealAsync(key);
        Assert.Equal(closeState, seal.CloseState);
        // Three addressed, one acknowledged, one dismissed: five the reviewer was right about, one it was not.
        Assert.Equal(5, seal.AddressedCount + seal.AcknowledgedCount + seal.DismissedCount);
        Assert.Equal(1, seal.FalsePositiveCount);
        Assert.Equal(2, seal.MissCount);
        Assert.Equal(6, seal.ResolvedCount);
        Assert.Equal(5d / 6d, seal.Precision!.Value, 12);
        Assert.Equal(5d / 7d, seal.Recall!.Value, 12);
        Assert.Equal(4d / 6d, seal.AcceptanceRate!.Value, 12);
    }

    [Fact]
    public async Task TheStoredRatiosAreExactlyWhatTheStoredCountsProduce()
    {
        // The reproducibility criterion, end to end: a report may re-derive a metric from the counts and must
        // land on the same value the seal shows.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 4, acknowledged: 2, dismissed: 3, falsePositive: 5);
        await this.SeedMissesAsync(key, qualifying: 6, disqualified: 0);
        await this._sealer.SealAsync(key, "Completed");

        var seal = await this.LoadSealAsync(key);
        var recomputed = CodeInsightMetricCalculator.Compute(
            new CodeInsightMetricInputs(
                seal.AddressedCount,
                seal.AcknowledgedCount,
                seal.DismissedCount,
                seal.FalsePositiveCount,
                seal.MissCount));

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(recomputed.F1!.Value),
            BitConverter.DoubleToInt64Bits(seal.F1!.Value));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(recomputed.Precision!.Value),
            BitConverter.DoubleToInt64Bits(seal.Precision!.Value));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(recomputed.Recall!.Value),
            BitConverter.DoubleToInt64Bits(seal.Recall!.Value));
    }

    [Fact]
    public async Task AFindingStillOpenAtCloseIsExcludedRatherThanCountedAsAnything()
    {
        // Nobody ever said whether it was right, so it is neither a true positive nor a false one. The count is
        // kept so the smaller denominator is explained by the data.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 0, open: 5);

        await this._sealer.SealAsync(key, "Completed");

        var seal = await this.LoadSealAsync(key);
        Assert.Equal(2, seal.ResolvedCount);
        Assert.Equal(5, seal.OpenAtSealCount);
        Assert.Equal(1d, seal.Precision);
    }

    [Fact]
    public async Task ASecondCloseLeavesTheFirstSealExactlyAsItWas()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 1, acknowledged: 0, dismissed: 0, falsePositive: 0, open: 2);
        await this._sealer.SealAsync(key, "Completed");
        var first = await this.LoadSealAsync(key);

        // The pull request is reopened, two more findings resolve as wrong, and it closes again. A number a
        // report has already shown does not get to move.
        await this.ResolveOpenFindingsAsync(key, CodeInsightDisposition.FalsePositive, 2);
        Assert.False(await this._sealer.SealAsync(key, "Abandoned"));

        var second = await this.LoadSealAsync(key);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.SealedAt, second.SealedAt);
        Assert.Equal("Completed", second.CloseState);
        Assert.Equal(1, second.ResolvedCount);
        Assert.Equal(1d, second.Precision);
    }

    [Fact]
    public async Task NothingIsSealedWhenTheGateIsClosed()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 1);
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await this._sealer.SealAsync(key, "Completed"));

        Assert.Empty(await this._dbContext.CodeInsightPullRequestMetrics.ToListAsync());
    }

    [Fact]
    public async Task APullRequestThatClosedWithNothingMeasurableIsNotSealed()
    {
        // Every finding still open and no miss harvested. A row of undefined ratios would still count toward the
        // sample size of a metric it says nothing about.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 0, acknowledged: 0, dismissed: 0, falsePositive: 0, open: 4);

        Assert.False(await this._sealer.SealAsync(key, "Completed"));

        Assert.Empty(await this._dbContext.CodeInsightPullRequestMetrics.ToListAsync());
    }

    [Fact]
    public async Task NothingCollectedForThePullRequestSealsNothing()
    {
        // Collection was switched on after this pull request was reviewed. There is no aggregate to measure and
        // no placeholder is invented.
        var key = new CodeInsightPullRequestKey(ClientA, "repo-1", 99);

        Assert.False(await this._sealer.SealAsync(key, "Completed"));

        Assert.Empty(await this._dbContext.CodeInsightPullRequestMetrics.ToListAsync());
    }

    [Fact]
    public async Task OnlyQualifyingMissesCountTowardRecall()
    {
        // The disqualified ones were harvested to make the cut-off inspectable. Counting them would charge the
        // reviewer for the questions and nits it was right to leave alone.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 0);
        await this.SeedMissesAsync(key, qualifying: 1, disqualified: 4);

        await this._sealer.SealAsync(key, "Completed");

        var seal = await this.LoadSealAsync(key);
        Assert.Equal(1, seal.MissCount);
        Assert.Equal(3d / 4d, seal.Recall!.Value, 12);
    }

    [Fact]
    public async Task AggregationSumsTheStoredInputsRatherThanAveragingTheRatios()
    {
        // The single most likely way to get this wrong. One pull request scores a perfect precision on one
        // finding; the other is right twice out of eight. Averaging says 0.63; the honest answer is 0.33.
        var perfect = await this.SeedAsync(ClientA, "repo-1", 1, addressed: 1, acknowledged: 0, dismissed: 0, falsePositive: 0);
        var poor = await this.SeedAsync(ClientA, "repo-1", 2, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 6);
        await this._sealer.SealAsync(perfect, "Completed");
        await this._sealer.SealAsync(poor, "Completed");

        var repository = await this._reader.GetCorrectnessAsync(this.Window(ClientA));

        Assert.Equal(3d / 9d, repository.Metrics.Precision!.Value, 12);
        Assert.Equal(2, repository.SampleSize);

        var averaged = (1d + (2d / 8d)) / 2d;
        Assert.NotEqual(averaged, repository.Metrics.Precision!.Value, 3);
    }

    [Fact]
    public async Task PullRequestResultsRollUpToRepositoryAndClient()
    {
        await this.SealAsync(ClientA, "repo-1", 1, addressed: 2, falsePositive: 1);
        await this.SealAsync(ClientA, "repo-1", 2, addressed: 1, falsePositive: 0);
        await this.SealAsync(ClientA, "repo-2", 3, addressed: 3, falsePositive: 2);

        var window = this.Window(ClientA);
        var byPullRequest = await this._reader.GetCorrectnessByGrainAsync(window, CodeInsightGrain.PullRequest);
        var byRepository = await this._reader.GetCorrectnessByGrainAsync(window, CodeInsightGrain.Repository);
        var byClient = await this._reader.GetCorrectnessByGrainAsync(window, CodeInsightGrain.Client);

        Assert.Equal(3, byPullRequest.Count);
        Assert.Equal(2, byRepository.Count);
        var client = Assert.Single(byClient);

        // Each grain is the sum of the one below it, on the counts.
        Assert.Equal(6, client.Result.Metrics.Inputs.TruePositives);
        Assert.Equal(3, client.Result.Metrics.Inputs.FalsePositives);
        Assert.Equal(6d / 9d, client.Result.Metrics.Precision!.Value, 12);
        Assert.Equal(
            byRepository.Sum(row => row.Result.Metrics.Inputs.TruePositives),
            client.Result.Metrics.Inputs.TruePositives);
        Assert.Equal(3, client.Result.SampleSize);
        Assert.Equal(0, client.Result.Metrics.Inputs.Misses);
    }

    [Fact]
    public async Task AnotherClientsSealsNeverEnterTheResult()
    {
        // The tenancy filter is unconditional, and this is the defect that would cost the most if it slipped.
        await this.SealAsync(ClientA, "repo-1", 1, addressed: 1, falsePositive: 0);
        await this.SealAsync(ClientB, "repo-1", 1, addressed: 0, falsePositive: 9);

        var result = await this._reader.GetCorrectnessAsync(this.Window(ClientA));

        Assert.Equal(1, result.SampleSize);
        Assert.Equal(1d, result.Metrics.Precision);
    }

    [Fact]
    public async Task AnEmptyAuthorisedClientSetYieldsNothingRatherThanEverything()
    {
        await this.SealAsync(ClientA, "repo-1", 1, addressed: 1, falsePositive: 0);

        var result = await this._reader.GetCorrectnessAsync(this.Window());

        Assert.Equal(0, result.SampleSize);
        Assert.Null(result.Metrics.Precision);
    }

    [Fact]
    public async Task ASealOutsideTheWindowIsNotCounted()
    {
        await this.SealAsync(ClientA, "repo-1", 1, addressed: 1, falsePositive: 0);

        // Sealing happens now, so a window that ended yesterday must not contain it.
        var past = new CodeInsightRollupQuery(
            [ClientA],
            new DateOnly(2020, 1, 1),
            new DateOnly(2020, 12, 31));

        var result = await this._reader.GetCorrectnessAsync(past);

        Assert.Equal(0, result.SampleSize);
        Assert.Null(result.Metrics.F1);
    }

    [Fact]
    public async Task AcceptanceRateIsAvailableWithNoSealedPullRequestAtAll()
    {
        // It is the early signal: answerable on the first day, long before anything has closed.
        await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 1, dismissed: 1, falsePositive: 1);

        var acceptance = await this._reader.GetAcceptanceAsync(this.Window(ClientA));
        var correctness = await this._reader.GetCorrectnessAsync(this.Window(ClientA));

        Assert.Equal(0, correctness.SampleSize);
        Assert.Equal(6, acceptance.SampleSize);
        Assert.Equal(4d / 6d, acceptance.Metrics.AcceptanceRate!.Value, 12);
        Assert.Equal(0, acceptance.Metrics.Inputs.Misses);
    }

    [Fact]
    public async Task AcceptanceRateIsUndefinedRatherThanZeroBeforeAnythingResolves()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, addressed: 0, acknowledged: 0, dismissed: 0, falsePositive: 0, open: 4);

        var acceptance = await this._reader.GetAcceptanceAsync(this.Window(ClientA));

        Assert.Equal(0, acceptance.SampleSize);
        Assert.Null(acceptance.Metrics.AcceptanceRate);
    }

    [Fact]
    public async Task AcceptanceRateNarrowsToWhicheverScopeTheQueryAsksFor()
    {
        await this.SeedAsync(ClientA, "repo-1", 1, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 0);
        await this.SeedAsync(ClientA, "repo-2", 2, addressed: 0, acknowledged: 0, dismissed: 0, falsePositive: 3);

        var everything = await this._reader.GetAcceptanceAsync(this.Window(ClientA));
        var firstRepository = await this._reader.GetAcceptanceAsync(this.Window(ClientA) with { RepositoryId = "repo-1" });

        Assert.Equal(2d / 5d, everything.Metrics.AcceptanceRate!.Value, 12);
        Assert.Equal(1d, firstRepository.Metrics.AcceptanceRate!.Value);
        Assert.Equal(2, firstRepository.SampleSize);
    }

    [Fact]
    public async Task TheCorrectnessSeriesBucketsSealsByWhenTheyWereSealed()
    {
        // A seal is immutable once written, so two seals cannot be dated apart by re-sealing. They are written
        // directly here: the sealer's own behaviour is covered above, and what this pins is the bucketing.
        await this.WriteSealAsync("repo-1", 1, new DateOnly(2026, 5, 4), addressed: 2, falsePositive: 0);
        await this.WriteSealAsync("repo-1", 2, new DateOnly(2026, 6, 9), addressed: 1, falsePositive: 1);

        var series = await this._reader.GetCorrectnessSeriesAsync(this.Window(ClientA), CodeInsightBucketSize.Month);

        Assert.Equal(2, series.Count);
        Assert.Equal(new DateOnly(2026, 5, 1), series[0].BucketStart);
        Assert.Equal(new DateOnly(2026, 6, 1), series[1].BucketStart);
        Assert.All(series, point => Assert.Equal(1, point.Result.SampleSize));
        // Each bucket is computed from its own counts, not from the window's.
        Assert.Equal(1d, series[0].Result.Metrics.Precision);
        Assert.Equal(0.5d, series[1].Result.Metrics.Precision);
    }

    [Fact]
    public async Task WeeksAreAnchoredToTheirMondaySoAWeekIsTheSameWeekForEveryReader()
    {
        // 2026-06-10 is a Wednesday; 2026-06-14 the Sunday after. Both belong to the week beginning the 8th.
        await this.WriteSealAsync("repo-1", 1, new DateOnly(2026, 6, 10), addressed: 1, falsePositive: 0);
        await this.WriteSealAsync("repo-1", 2, new DateOnly(2026, 6, 14), addressed: 1, falsePositive: 0);

        var series = await this._reader.GetCorrectnessSeriesAsync(this.Window(ClientA), CodeInsightBucketSize.Week);

        var point = Assert.Single(series);
        Assert.Equal(new DateOnly(2026, 6, 8), point.BucketStart);
        Assert.Equal(2, point.Result.SampleSize);
    }

    [Fact]
    public async Task TheAcceptanceSeriesBucketsFindingsByWhenTheyWereReviewed()
    {
        // Acceptance is a cohort: a period holds the findings reviewed in it, so a late outcome matures that
        // period rather than today's.
        await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 1);

        var series = await this._reader.GetAcceptanceSeriesAsync(this.Window(ClientA), CodeInsightBucketSize.Month);

        var point = Assert.Single(series);
        Assert.Equal(new DateOnly(ReviewedAt.Year, ReviewedAt.Month, 1), point.BucketStart);
        Assert.Equal(3d / 4d, point.Result.Metrics.AcceptanceRate!.Value, 12);
        Assert.Equal(4, point.Result.SampleSize);
    }

    [Fact]
    public async Task AnEmptyWindowYieldsNoSeriesPointsRatherThanPointsAtZero()
    {
        var correctness = await this._reader.GetCorrectnessSeriesAsync(this.Window(ClientA), CodeInsightBucketSize.Week);
        var acceptance = await this._reader.GetAcceptanceSeriesAsync(this.Window(ClientA), CodeInsightBucketSize.Week);

        Assert.Empty(correctness);
        Assert.Empty(acceptance);
    }

    [Fact]
    public async Task GroupingByRepositoryNamesItWhenAReviewReportedOne()
    {
        // The seals key on the provider's repository identifier; a ranked table of bare numbers is not a ranking
        // an operator can act on.
        await this.SealAsync(ClientA, "4", 7, addressed: 3, falsePositive: 1);
        await this.SealAsync(ClientA, "9", 8, addressed: 2, falsePositive: 0);
        await this._store.TouchPullRequestAsync(
            new CodeInsightPullRequestKey(ClientA, "4", 7),
            "Completed",
            ReviewedAt,
            "payments-api");

        var rows = await this._reader.GetCorrectnessByGrainAsync(this.Window(ClientA), CodeInsightGrain.Repository);

        Assert.Equal("payments-api", rows.Single(row => row.RepositoryId == "4").RepositoryName);
        Assert.Null(rows.Single(row => row.RepositoryId == "9").RepositoryName);
    }

    [Fact]
    public async Task GroupingByModelSumsWhatEachModelProducedRatherThanAveragingPullRequests()
    {
        // The reading that answers "would the cheap model have done": two models, both across two pull requests.
        await this.SeedAsync(
            ClientA, "repo-1", 1, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 1, modelId: "cheap-1", logicalModelName: "thrifty");
        await this.SeedAsync(
            ClientA, "repo-2", 2, addressed: 1, acknowledged: 0, dismissed: 0, falsePositive: 5, modelId: "cheap-1", logicalModelName: "thrifty");
        await this.SeedAsync(
            ClientA, "repo-1", 3, addressed: 8, acknowledged: 1, dismissed: 0, falsePositive: 1, modelId: "dear-1", logicalModelName: "balanced");

        var rows = await this._reader.GetByModelAsync(this.Window(ClientA));

        Assert.Equal(2, rows.Count);

        // Worst first, on the only correctness ratio a model can be held to.
        var thrifty = rows[0];
        Assert.Equal("thrifty", thrifty.LogicalModelName);
        Assert.Equal("cheap-1", thrifty.ModelId);
        // Four right and six wrong across both pull requests, not the mean of 0.75 and 0.167.
        Assert.Equal(4d / 10d, thrifty.Result.Metrics.Precision!.Value, 12);
        Assert.Equal(10, thrifty.Result.SampleSize);

        var balanced = rows[1];
        Assert.Equal("balanced", balanced.LogicalModelName);
        Assert.Equal(9d / 10d, balanced.Result.Metrics.Precision!.Value, 12);
    }

    [Fact]
    public async Task RecallIsUndefinedPerModelBecauseAMissHasNoProducingModel()
    {
        // Zero would hand a model a recall it never earned; the view has to render "—" instead.
        var key = await this.SeedAsync(ClientA, "repo-1", 1, addressed: 4, acknowledged: 0, dismissed: 0, falsePositive: 1, modelId: "cheap-1");
        await this.SeedMissesAsync(key, qualifying: 7, disqualified: 0);

        var row = Assert.Single(await this._reader.GetByModelAsync(this.Window(ClientA)));

        Assert.Null(row.Result.Metrics.Recall);
        Assert.Null(row.Result.Metrics.F1);
        Assert.Equal(0, row.Result.Metrics.Inputs.Misses);
        Assert.NotNull(row.Result.Metrics.Precision);
        Assert.NotNull(row.Result.Metrics.AcceptanceRate);
    }

    [Fact]
    public async Task FindingsWithNoRecordedModelGroupAsUnattributedRatherThanBeingDropped()
    {
        // Reviews that ran before models were recorded still resolved, and dropping them would quietly change the
        // total the per-model rows add up to.
        await this.SeedAsync(ClientA, "repo-1", 1, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 1, modelId: "cheap-1");
        await this.SeedAsync(ClientA, "repo-1", 2, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 0);

        var rows = await this._reader.GetByModelAsync(this.Window(ClientA));

        var unattributed = Assert.Single(rows, row => row.ModelId is null && row.LogicalModelName is null);
        Assert.Equal(3, unattributed.Result.SampleSize);
        Assert.Equal(6, rows.Sum(row => row.Result.SampleSize));
    }

    [Fact]
    public async Task ALogicalNameRepointedAtAnotherModelReportsAsTwoRows()
    {
        // The whole reason both identities are stored: merging these would compare a model with itself.
        await this.SeedAsync(
            ClientA, "repo-1", 1, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 2, modelId: "old-remote", logicalModelName: "balanced");
        await this.SeedAsync(
            ClientA, "repo-1", 2, addressed: 4, acknowledged: 0, dismissed: 0, falsePositive: 0, modelId: "new-remote", logicalModelName: "balanced");

        var rows = await this._reader.GetByModelAsync(this.Window(ClientA));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("balanced", row.LogicalModelName));
        Assert.Equal(["old-remote", "new-remote"], rows.Select(row => row.ModelId));
    }

    [Fact]
    public async Task AModelWithNothingResolvedYetIsAbsentRatherThanPresentAtZero()
    {
        await this.SeedAsync(ClientA, "repo-1", 1, addressed: 0, acknowledged: 0, dismissed: 0, falsePositive: 0, open: 4, modelId: "cheap-1");

        Assert.Empty(await this._reader.GetByModelAsync(this.Window(ClientA)));
    }

    [Fact]
    public async Task ThePerModelReadRefusesToAggregateOverClientsTheCallerDidNotSupply()
    {
        await this.SeedAsync(ClientA, "repo-1", 1, addressed: 2, acknowledged: 0, dismissed: 0, falsePositive: 1, modelId: "cheap-1");
        await this.SeedAsync(ClientB, "repo-9", 2, addressed: 5, acknowledged: 0, dismissed: 0, falsePositive: 0, modelId: "cheap-1");

        var row = Assert.Single(await this._reader.GetByModelAsync(this.Window(ClientA)));

        Assert.Equal(3, row.Result.SampleSize);
        Assert.Empty(await this._reader.GetByModelAsync(new CodeInsightRollupQuery([], new DateOnly(2026, 1, 1), new DateOnly(2099, 12, 31))));
    }

    [Fact]
    public async Task An_unresolved_discussion_is_counted_and_kept_out_of_both_lenses()
    {
        // Neither accepted nor rejected. The count travels so the volume is visible; the ratios do not move.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, addressed: 3, acknowledged: 0, dismissed: 0, falsePositive: 1, open: 2);
        await this.ResolveOpenFindingsAsync(key, CodeInsightDisposition.Discussed, 2);

        // The acceptance lens reads the projection, so the two late outcomes have to be projected like any
        // others. The projector groups by the outcome's own name, so a new outcome needs nothing added to it.
        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        await this._projector.ProjectJobAsync(findings[0].JobId);

        var acceptance = await this._reader.GetAcceptanceAsync(this.Window(ClientA));

        // Four findings reached a verdict, so that is what the acceptance rate is a proportion of.
        Assert.Equal(4, acceptance.Metrics.Inputs.Resolved);
        Assert.Equal(2, acceptance.Metrics.Inputs.Discussed);
        Assert.Equal(3d / 4d, acceptance.Metrics.AcceptanceRate!.Value, 12);
        Assert.Equal(4, acceptance.SampleSize);

        Assert.True(await this._sealer.SealAsync(key, "Completed"));
        var seal = await this.LoadSealAsync(key);
        Assert.Equal(2, seal.DiscussedCount);
        // The seal's own denominator excludes them too.
        Assert.Equal(4, seal.ResolvedCount);
        Assert.Equal(3d / 4d, seal.Precision!.Value, 12);
    }

    [Fact]
    public async Task Rejection_reasons_are_counted_per_reason_with_the_unclassified_remainder_reported()
    {
        // A precision number says how often the reviewer was turned down. This says what to do about it, and an
        // unjudged reason must be visible rather than folded into one of the five.
        var key = await this.SeedRejectionsAsync(
            ClientA,
            "repo-1",
            41,
            [
                CodeInsightRejectionReason.Wrong,
                CodeInsightRejectionReason.Wrong,
                CodeInsightRejectionReason.OutOfScope,
                null,
            ]);
        Assert.NotNull(key);

        var breakdown = await this._reader.GetRejectionReasonsAsync(this.Window(ClientA));

        Assert.Equal(2, breakdown.Counts[CodeInsightRejectionReason.Wrong]);
        Assert.Equal(1, breakdown.Counts[CodeInsightRejectionReason.OutOfScope]);
        Assert.False(breakdown.Counts.ContainsKey(CodeInsightRejectionReason.Redundant));
        Assert.Equal(1, breakdown.Unclassified);
        // The reasons plus the unclassified remainder account for every rejection, with nothing lost or double
        // counted between them.
        Assert.Equal(4, breakdown.Rejections);
        Assert.Equal(breakdown.Rejections, breakdown.Counts.Values.Sum() + breakdown.Unclassified);
    }

    [Fact]
    public async Task Only_rejections_reach_the_reason_distribution()
    {
        // A fixed or accepted finding was never rejected, so it belongs to neither a reason nor the
        // unclassified remainder.
        await this.SeedAsync(ClientA, "repo-1", 41, addressed: 3, acknowledged: 2, dismissed: 1, falsePositive: 1);

        var breakdown = await this._reader.GetRejectionReasonsAsync(this.Window(ClientA));

        Assert.Equal(2, breakdown.Rejections);
        Assert.Equal(2, breakdown.Unclassified);
        Assert.Empty(breakdown.Counts);
    }

    [Fact]
    public async Task The_reason_distribution_stays_inside_the_callers_clients()
    {
        await this.SeedRejectionsAsync(ClientA, "repo-1", 41, [CodeInsightRejectionReason.Wrong]);
        await this.SeedRejectionsAsync(ClientB, "repo-2", 42, [CodeInsightRejectionReason.Redundant]);

        var breakdown = await this._reader.GetRejectionReasonsAsync(this.Window(ClientA));

        Assert.Equal(1, breakdown.Rejections);
        Assert.Equal(1, breakdown.Counts[CodeInsightRejectionReason.Wrong]);
        Assert.False(breakdown.Counts.ContainsKey(CodeInsightRejectionReason.Redundant));

        Assert.Equal(
            CodeInsightRejectionReasonBreakdown.Empty,
            await this._reader.GetRejectionReasonsAsync(this.Window()));
    }

    [Fact]
    public async Task Rejection_reasons_are_also_grouped_by_the_kind_of_concern_raised()
    {
        // The published finding this follows: functional and evolvability findings are rejected at similar rates
        // for entirely different reasons, so a single distribution averages the difference away.
        var key = await this.SeedRejectionsAsync(
            ClientA,
            "repo-1",
            41,
            [
                CodeInsightRejectionReason.Wrong,
                CodeInsightRejectionReason.Wrong,
                CodeInsightRejectionReason.DeveloperPreference,
                null,
            ]);

        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        // Two functional findings, one evolvability, and one left with no type at all.
        await this.ClassifyAsync(findings[0].Id, CodeInsightCoreTaxonomy.LogicError);
        await this.ClassifyAsync(findings[1].Id, CodeInsightCoreTaxonomy.Security);
        await this.ClassifyAsync(findings[2].Id, CodeInsightCoreTaxonomy.NamingClarity);

        var breakdown = await this._reader.GetRejectionReasonsAsync(this.Window(ClientA));

        var functional = breakdown.ByConcernClass.Single(row => row.ConcernClass == CodeInsightConcernClass.Functional);
        Assert.Equal(2, functional.Counts[CodeInsightRejectionReason.Wrong]);
        Assert.Equal(2, functional.Rejections);

        var evolvability = breakdown.ByConcernClass.Single(row => row.ConcernClass == CodeInsightConcernClass.Evolvability);
        Assert.Equal(1, evolvability.Counts[CodeInsightRejectionReason.DeveloperPreference]);
        Assert.False(evolvability.Counts.ContainsKey(CodeInsightRejectionReason.Wrong));

        // A finding with no core type belongs to neither class and is reported as its own row rather than dropped.
        var unclassified = breakdown.ByConcernClass.Single(row => row.ConcernClass is null);
        Assert.Equal(1, unclassified.Rejections);
        Assert.Equal(1, unclassified.WithoutReason);

        // The classes account for every rejection between them, with none double counted.
        Assert.Equal(breakdown.Rejections, breakdown.ByConcernClass.Sum(row => row.Rejections));
        // Functional first, then evolvability, then the untyped remainder, whatever order the rows arrived in.
        Assert.Equal(
            [CodeInsightConcernClass.Functional, CodeInsightConcernClass.Evolvability, null],
            breakdown.ByConcernClass.Select(row => row.ConcernClass));
    }

    [Fact]
    public async Task A_finding_typed_in_both_classes_is_counted_as_functional_only()
    {
        var key = await this.SeedRejectionsAsync(ClientA, "repo-1", 41, [CodeInsightRejectionReason.Wrong]);
        var finding = (await this._store.GetFindingsForPullRequestAsync(key))[0];
        await this.ClassifyAsync(finding.Id, CodeInsightCoreTaxonomy.NamingClarity, CodeInsightCoreTaxonomy.LogicError);

        var breakdown = await this._reader.GetRejectionReasonsAsync(this.Window(ClientA));

        var row = Assert.Single(breakdown.ByConcernClass);
        Assert.Equal(CodeInsightConcernClass.Functional, row.ConcernClass);
        Assert.Equal(1, row.Rejections);
    }

    private Task ClassifyAsync(Guid findingId, params string[] coreSlugs)
    {
        return this._store.ApplyClassificationAsync(
            findingId,
            new CodeInsightClassification(
                coreSlugs,
                [],
                CodeInsightFindingLevel.File,
                CodeInsightFindingQualifier.Incorrect,
                0.9,
                "test-types"));
    }

    private async Task<CodeInsightPullRequestKey> SeedRejectionsAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        IReadOnlyList<CodeInsightRejectionReason?> reasons)
    {
        var key = await this.SeedAsync(
            clientId,
            repositoryId,
            pullRequestId,
            addressed: 0,
            acknowledged: 0,
            dismissed: 0,
            falsePositive: 0,
            open: reasons.Count);

        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        foreach (var (finding, reason) in findings.Zip(reasons))
        {
            await this._store.RecordDispositionAsync(
                finding.Id,
                new CodeInsightDispositionRecord(
                    reason == CodeInsightRejectionReason.Wrong
                        ? CodeInsightDisposition.FalsePositive
                        : CodeInsightDisposition.Dismissed,
                    ThreadResolutionIntent.Active,
                    ThreadAnchorCodeChange.Unchanged,
                    "test-split",
                    0.7,
                    reason));
        }

        return key;
    }

    private CodeInsightRollupQuery Window(params Guid[] clientIds)
    {
        return new CodeInsightRollupQuery(
            clientIds,
            new DateOnly(2026, 1, 1),
            new DateOnly(2099, 12, 31));
    }

    /// <summary>
    ///     Writes a seal with a chosen seal date, for the series tests. A real seal always stamps the current
    ///     instant and is immutable, so dating two of them apart is only possible from outside the sealer.
    /// </summary>
    private async Task WriteSealAsync(
        string repositoryId,
        long pullRequestId,
        DateOnly sealedOn,
        int addressed,
        int falsePositive)
    {
        var key = await this.SeedAsync(ClientA, repositoryId, pullRequestId, addressed, 0, 0, falsePositive);
        var aggregateId = await this._dbContext.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => candidate.Id)
            .SingleAsync();

        var inputs = new CodeInsightMetricInputs(addressed, 0, 0, falsePositive, 0);
        var metrics = CodeInsightMetricCalculator.Compute(inputs);

        this._dbContext.CodeInsightPullRequestMetrics.Add(
            new Domain.Entities.CodeInsightPullRequestMetric
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = aggregateId,
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                AddressedCount = inputs.Addressed,
                FalsePositiveCount = inputs.FalsePositive,
                ResolvedCount = inputs.Resolved,
                Precision = metrics.Precision,
                Recall = metrics.Recall,
                F1 = metrics.F1,
                AcceptanceRate = metrics.AcceptanceRate,
                CloseState = "Completed",
                SealedAt = new DateTimeOffset(sealedOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                SealedOn = sealedOn,
            });

        await this._dbContext.SaveChangesAsync();
    }

    /// <summary>Seals a pull request whose resolved findings are the given mix, for the roll-up tests.</summary>
    private async Task SealAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        int addressed,
        int falsePositive)
    {
        var key = await this.SeedAsync(clientId, repositoryId, pullRequestId, addressed, 0, 0, falsePositive);
        await this._sealer.SealAsync(key, "Completed");
    }

    /// <summary>
    ///     Materialises findings for one pull request and records the requested outcomes against them, leaving
    ///     <paramref name="open" /> of them undecided.
    /// </summary>
    private async Task<CodeInsightPullRequestKey> SeedAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        int addressed,
        int acknowledged,
        int dismissed,
        int falsePositive,
        int open = 0,
        string? modelId = null,
        string? logicalModelName = null)
    {
        var jobId = Guid.NewGuid();
        var key = new CodeInsightPullRequestKey(clientId, repositoryId, pullRequestId);
        var outcomes = new List<CodeInsightDisposition?>();
        outcomes.AddRange(Enumerable.Repeat((CodeInsightDisposition?)CodeInsightDisposition.Addressed, addressed));
        outcomes.AddRange(Enumerable.Repeat((CodeInsightDisposition?)CodeInsightDisposition.Acknowledged, acknowledged));
        outcomes.AddRange(Enumerable.Repeat((CodeInsightDisposition?)CodeInsightDisposition.Dismissed, dismissed));
        outcomes.AddRange(Enumerable.Repeat((CodeInsightDisposition?)CodeInsightDisposition.FalsePositive, falsePositive));
        outcomes.AddRange(Enumerable.Repeat((CodeInsightDisposition?)null, open));

        var snapshots = outcomes
            .Select((_, ordinal) => new CodeInsightFindingSnapshot(
                ordinal,
                "a.cs",
                10 + ordinal,
                CommentSeverity.Error,
                $"Finding {ordinal}",
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                null,
                $"thread-{jobId:N}-{ordinal}",
                $"comment-{jobId:N}-{ordinal}",
                modelId,
                logicalModelName))
            .ToList();

        await this._store.MaterialiseFindingsAsync(key, jobId, $"rev-{jobId:N}", ReviewedAt, snapshots);

        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        foreach (var (finding, outcome) in findings.Zip(outcomes))
        {
            if (outcome is null)
            {
                continue;
            }

            await this._store.RecordDispositionAsync(finding.Id, Disposition(outcome.Value));
        }

        // The acceptance lens reads the projection, so it has to reflect what was just recorded.
        await this._projector.ProjectJobAsync(jobId);
        return key;
    }

    private async Task ResolveOpenFindingsAsync(
        CodeInsightPullRequestKey key,
        CodeInsightDisposition disposition,
        int count)
    {
        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        var resolved = 0;

        foreach (var finding in findings)
        {
            if (resolved == count)
            {
                break;
            }

            if (await this._store.GetDispositionAsync(finding.Id) is not null)
            {
                continue;
            }

            await this._store.RecordDispositionAsync(finding.Id, Disposition(disposition));
            resolved++;
        }
    }

    private async Task SeedMissesAsync(CodeInsightPullRequestKey key, int qualifying, int disqualified)
    {
        for (var index = 0; index < qualifying + disqualified; index++)
        {
            var counts = index < qualifying;
            await this._store.RecordMissAsync(
                key,
                new CodeInsightMissRecord(
                    $"human-thread-{key.PullRequestId}-{index}",
                    "a.cs",
                    20 + index,
                    "A human raised something here.",
                    IsSubstantive: counts,
                    WasActedOn: counts,
                    IsInScope: counts,
                    CountsAsMiss: counts,
                    0.9,
                    "test"));
        }
    }

    private async Task<Domain.Entities.CodeInsightPullRequestMetric> LoadSealAsync(CodeInsightPullRequestKey key)
    {
        var aggregateId = await this._dbContext.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => candidate.Id)
            .SingleAsync();

        return await this._dbContext.CodeInsightPullRequestMetrics
            .SingleAsync(metric => metric.CodeInsightPullRequestId == aggregateId);
    }

    private static CodeInsightDispositionRecord Disposition(CodeInsightDisposition disposition)
    {
        return new CodeInsightDispositionRecord(
            disposition,
            ThreadResolutionIntent.ClaimsFix,
            ThreadAnchorCodeChange.Changed,
            null,
            null);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightMetricTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
