// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit.Abstractions;

namespace MeisterDev.ProPR.CodeInsights.Tests.Simulation;

/// <summary>
///     Drives the production close paths over many simulated crawl cycles to observe what the reported
///     correctness metric does.
/// </summary>
/// <remarks>
///     A close reaches the metric two ways and both run here as shipped: lifecycle synchronization through
///     <see cref="PullRequestSynchronizationService" />, and <see cref="CodeInsightSealSweeper" /> for a close
///     no pass saw. The harvest, the store, the sealer and the reader are the production types. Substituted:
///     the classifier, the provider fetch, the job repository and the collection gate, so no model is called and
///     no network request is made. What the simulation supplies itself is the crawl loop that would drive these
///     over days, the threads the provider would return, and the active pass's harvest, which stands in for the
///     thread observation the crawl runs while a pull request is open.
/// </remarks>
public sealed class F1RecoverySimulation(ITestOutputHelper output) : IDisposable
{
    private static readonly Guid ClientId = Guid.Parse("42424242-4242-4242-4242-424242424242");
    private const string Repository = "repo-sim";
    private static readonly Guid HumanAuthorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly MeisterProPRDbContext _db = NewContext();
    private readonly List<string> _log = [];
    private readonly List<string> _keyDirectories = [];
    private readonly HashSet<Guid> _cancelledJobs = [];

    /// <summary>What the most recent lifecycle synchronization decided, for the experiments that drive one.</summary>
    private PullRequestSynchronizationOutcome? LastLifecycleOutcome { get; set; }

    private readonly List<ServiceProvider> _codecProviders = [];

    private CodeInsightFindingStore _store = null!;
    private CodeInsightMissHarvester _harvester = null!;
    private PullRequestCloseObserver _closeObserver = null!;
    private PullRequestSynchronizationService _synchronization = null!;
    private IJobRepository _jobs = null!;
    private CodeInsightDispositionService _dispositions = null!;
    private CodeInsightMetricSealer _sealer = null!;
    private CodeInsightSealSweeper _sweeper = null!;
    private CodeInsightMetricReader _reader = null!;
    private IHumanMissClassifier _classifier = null!;
    private IPullRequestFetcher _provider = null!;
    private Func<long, PrStatus> _providerStatus = _ => PrStatus.Active;

    /// <summary>How a human thread reaches its resolved state, relative to the pull request's close.</summary>
    public enum ResolutionMoment
    {
        /// <summary>Never resolves: the thread is still open when the pull request closes.</summary>
        NeverResolves,

        /// <summary>Resolved several crawl cycles before the close, so a later Active pass observes it.</summary>
        WhileStillOpen,

        /// <summary>Resolved by the act of closing the pull request, which is the common provider behaviour.</summary>
        AtClose,
    }

    // ---------------------------------------------------------------- experiments

    [Fact]
    public async Task E1_AThreadThatNeverResolves_LeavesRecallUndefinedRatherThanPerfect()
    {
        this.Build();

        // An unresolved thread has no settled answer to the acted-on question, so the closing observation
        // changes nothing for it and it costs no model call. What it must not do is let the pull request
        // report that the reviewer missed nothing.
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);
        await this.SeedPullRequestAsync(1, findings: 5, addressed: 5, humanThreads: 3, ResolutionMoment.NeverResolves);

        await this.RunCyclesAsync(cycles: 30, closePullRequestOnCycle: 20);

        var metrics = await this.ReadAsync();
        this.Report("E1 threads never resolve", metrics);

        Assert.Equal(0, metrics.Metrics.Inputs.Misses);
        Assert.Equal(1, metrics.SampleSize);
        Assert.Equal(0, metrics.CoveredSampleSize);
        Assert.Null(metrics.Metrics.Recall);
        Assert.Null(metrics.Metrics.F1);
        Assert.NotNull(metrics.Metrics.Precision);
    }

    [Fact]
    public async Task E10_ACloseTheCrawlSeesWhileAReviewJobIsStillActive_RecoversThroughTheLifecyclePath()
    {
        // The other experiments close with no active review job, so the sweep seals them. This one keeps a job
        // active so the crawl reaches lifecycle synchronization, which is the second path into the metric.
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);
        await this.SeedPullRequestAsync(
            10,
            findings: 5,
            addressed: 5,
            humanThreads: 3,
            ResolutionMoment.AtClose,
            reviewJobStillActiveAtClose: true);

        await this.RunCyclesAsync(cycles: 4, closePullRequestOnCycle: 3);

        var metrics = await this.ReadAsync();
        this.Report("E10 lifecycle close", metrics);

        // Five findings addressed, three threads the close resolved into misses.
        Assert.Equal(1, metrics.SampleSize);
        Assert.Equal(1, metrics.CoveredSampleSize);
        Assert.Equal(5, metrics.Metrics.Inputs.TruePositives);
        Assert.Equal(0, metrics.Metrics.Inputs.FalsePositives);
        Assert.Equal(3, metrics.Metrics.Inputs.Misses);
        Assert.Equal(5d / 8d, metrics.Metrics.Recall!.Value, 12);

        // The seal is only half of what this path does. Cancelling the review that was still running is the
        // other half, and it happens after the observation this experiment exists to prove.
        Assert.Equal(
            PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
            this.LastLifecycleOutcome?.LifecycleDecision);
        await this._jobs.Received(1).SetCancelledAsync(
            this._prs[10].ActiveJobId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task E2_ThreadResolvesWhileThePullRequestIsStillOpen_RecoversTheFalseNegative()
    {
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);
        await this.SeedPullRequestAsync(2, findings: 5, addressed: 5, humanThreads: 3, ResolutionMoment.WhileStillOpen);

        await this.RunCyclesAsync(cycles: 30, closePullRequestOnCycle: 20);

        var metrics = await this.ReadAsync();
        this.Report("E2 fix A reachable (threads resolve while open)", metrics);

        Assert.Equal(3, metrics.Metrics.Inputs.Misses);
        Assert.True(metrics.Metrics.Recall < 1d);
    }

    [Fact]
    public async Task E3_ThreadResolvesAsThePullRequestCloses_RecoversTheFalseNegative()
    {
        // The dominant production shape, and the case the closing observation exists for: before it, the
        // resolved state arrived only after the last Active pass and the judgement stayed provisional for good.
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);
        await this.SeedPullRequestAsync(3, findings: 5, addressed: 5, humanThreads: 3, ResolutionMoment.AtClose);

        await this.RunCyclesAsync(cycles: 30, closePullRequestOnCycle: 20);

        var metrics = await this.ReadAsync();
        this.Report("E3 threads resolve at close", metrics);

        Assert.Equal(1, metrics.SampleSize);
        Assert.Equal(1, metrics.CoveredSampleSize);
        Assert.Equal(5, metrics.Metrics.Inputs.TruePositives);
        Assert.Equal(0, metrics.Metrics.Inputs.FalsePositives);
        Assert.Equal(3, metrics.Metrics.Inputs.Misses);
        Assert.Equal(5d / 8d, metrics.Metrics.Recall!.Value, 12);
    }

    [Fact]
    public async Task E4_APullRequestWhereProPrRaisedNothing_IsNeverSealedBySweep()
    {
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

        // The worst-recall case there is: humans found things, ProPR found nothing.
        await this.SeedPullRequestAsync(4, findings: 0, addressed: 0, humanThreads: 4, ResolutionMoment.WhileStillOpen);

        await this.RunCyclesAsync(cycles: 30, closePullRequestOnCycle: 20);

        var qualifying = await this._db.CodeInsightMisses.CountAsync(miss => miss.CountsAsMiss);
        var seals = await this._db.CodeInsightPullRequestMetrics.CountAsync();
        this.Line($"E4 miss-only pull request: qualifying misses={qualifying}, seals={seals}");

        // The aggregate exists, created by the harvest itself, and carries no finding. Without asserting that,
        // a zero-seal result would also describe a sweep that never saw the pull request at all.
        var aggregate = await this._db.CodeInsightPullRequests.SingleAsync(row => row.PullRequestId == 4);
        Assert.Equal(0, await this._db.CodeInsightFindings.CountAsync(f => f.CodeInsightPullRequestId == aggregate.Id));
        Assert.True(aggregate.LastActivityAt < DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(4, qualifying);
        Assert.Equal(0, seals);
    }

    [Fact]
    public async Task E5_AMixedPopulation_ShowsHowFarTheAggregateActuallyMoves()
    {
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

        // 60 pull requests. Every one has findings that were addressed and human threads that qualify as
        // misses. They differ in one respect: when the human thread reaches its resolved state.
        var moments = new[]
        {
            (ResolutionMoment.AtClose, 42), // the common provider behaviour
            (ResolutionMoment.WhileStillOpen, 12), // resolved during review, several cycles before the close
            (ResolutionMoment.NeverResolves, 6), // left open
        };

        long id = 100;
        foreach (var (moment, count) in moments)
        {
            for (var i = 0; i < count; i++)
            {
                await this.SeedPullRequestAsync(id++, findings: 6, addressed: 5, humanThreads: 2, moment, falsePositive: 1);
            }
        }

        await this.RunCyclesAsync(cycles: 40, closePullRequestOnCycle: 20, reportEachCycle: true);

        var metrics = await this.ReadAsync();
        this.Report("E5 mixed population, final", metrics);

        var stored = await this._db.CodeInsightMisses.CountAsync(miss => miss.CountsAsMiss);
        this.Line($"E5 qualifying misses stored={stored}; misses that reached a seal={metrics.Metrics.Inputs.Misses}");

        // Both moments that reach a resolved state qualify, and the seals carry every one of them. Only the
        // six pull requests whose threads never resolve are left reporting nothing.
        Assert.Equal(60, metrics.SampleSize);
        Assert.Equal((42 + 12) * 2, stored);
        Assert.Equal(stored, metrics.Metrics.Inputs.Misses);
        Assert.NotNull(metrics.Metrics.Recall);
        Assert.True(metrics.Metrics.Recall < 1d);
    }

    [Fact]
    public async Task E6_ASealTakenBeforeAMissQualifies_NeverPicksItUp()
    {
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);
        await this.SeedPullRequestAsync(6, findings: 4, addressed: 4, humanThreads: 2, ResolutionMoment.WhileStillOpen);

        // Close first, so the seal is taken while every harvested judgement is still provisional.
        await this.RunCyclesAsync(cycles: 2, closePullRequestOnCycle: 1);
        var afterSeal = await this.ReadAsync();

        // Now let the threads resolve and be observed for many more cycles.
        this.ReopenWithEveryThreadResolved();
        await this.RunCyclesAsync(cycles: 20, closePullRequestOnCycle: null);

        var later = await this.ReadAsync();
        var qualifying = await this._db.CodeInsightMisses.CountAsync(miss => miss.CountsAsMiss);

        this.Report("E6 at seal", afterSeal);
        this.Report("E6 twenty cycles later", later);
        this.Line($"E6 qualifying misses now stored={qualifying}, misses in the sealed row={later.Metrics.Inputs.Misses}");

        // The seal was taken while the threads were still open, so it withholds recall instead of reporting a
        // perfect one. Twenty cycles later those threads have resolved and qualified, and the sealed row is
        // still what it was: the misses never reach it, and its recall stays withheld.
        Assert.Equal(1, afterSeal.SampleSize);
        Assert.Equal(0, afterSeal.CoveredSampleSize);
        Assert.Null(afterSeal.Metrics.Recall);
        Assert.True(qualifying > 0);
        Assert.Equal(0, later.CoveredSampleSize);
        Assert.Null(later.Metrics.Recall);
        Assert.Equal(0, later.Metrics.Inputs.Misses);
    }

    [Fact]
    public async Task E7_SweepingHowOftenAThreadResolvesBeforeTheClose_BoundsWhatTheFixCanMove()
    {
        // Everything else is held fixed: 40 pull requests, 8 findings each of which 7 were addressed and one
        // was wrong, and 3 human threads each of which is a genuine miss. Only the share of threads that reach
        // their resolved state while the pull request is still open varies.
        foreach (var openResolvedPercent in new[] { 0, 10, 25, 50, 75, 100 })
        {
            using var run = new F1RecoverySimulation(output);
            run.Build();
            run.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

            var resolvingEarly = 40 * openResolvedPercent / 100;
            for (long id = 1; id <= 40; id++)
            {
                await run.SeedPullRequestAsync(
                    id,
                    findings: 8,
                    addressed: 7,
                    humanThreads: 3,
                    id <= resolvingEarly ? ResolutionMoment.WhileStillOpen : ResolutionMoment.AtClose,
                    falsePositive: 1);
            }

            await run.RunCyclesAsync(cycles: 25, closePullRequestOnCycle: 15);

            var reported = await run.ReadAsync();

            this.Line(
                $"E7 resolved-before-close={openResolvedPercent,3}% | seeded misses=120 "
                + $"reported FN={reported.Metrics.Inputs.Misses,3} "
                + $"recall={Fmt(reported.Metrics.Recall)} F1={Fmt(reported.Metrics.F1)}");

            // When a thread resolves no longer changes what is reported: both close paths observe before they
            // seal, so every seeded miss is counted whatever moment it settled in. The seeded total is the
            // independent figure here: it is counted from the setup, not derived from the result.
            Assert.Equal(40 * 3, reported.Metrics.Inputs.Misses);
            Assert.Equal(40, reported.SampleSize);
        }
    }

    [Fact]
    public async Task E8_TheAcceptedWhileStillOpenYield_DominatesEverythingTheFixDoes()
    {
        // The judgement asks whether the concern was accepted OR led to a change, so a thread that is still
        // open can already answer yes on the strength of the reply alone. This sweep separates what the fix
        // contributes from what the classifier was already contributing without it.
        foreach (var (label, openYield) in new[] { ("never", false), ("always", true) })
        {
            foreach (var moment in new[] { ResolutionMoment.NeverResolves, ResolutionMoment.AtClose, ResolutionMoment.WhileStillOpen })
            {
                using var run = new F1RecoverySimulation(output);
                run.Build();
                run.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: openYield);

                for (long id = 1; id <= 20; id++)
                {
                    await run.SeedPullRequestAsync(id, findings: 8, addressed: 7, humanThreads: 3, moment, falsePositive: 1);
                }

                await run.RunCyclesAsync(cycles: 25, closePullRequestOnCycle: 15);

                var reported = await run.ReadAsync();
                this.Line(
                    $"E8 open-thread acted-on={label,-6} resolution={moment,-16} | FN={reported.Metrics.Inputs.Misses,3} "
                    + $"recall={Fmt(reported.Metrics.Recall)} F1={Fmt(reported.Metrics.F1)}");

                // A thread that resolves is counted however it got there. One that never resolves is counted
                // only when the classifier reads acceptance out of the discussion, which is the judgement
                // question this change does not settle.
                var expected = moment != ResolutionMoment.NeverResolves || openYield ? 60 : 0;
                Assert.Equal(expected, reported.Metrics.Inputs.Misses);

                // Twenty pull requests sealed either way. Without this a scenario that sealed nothing would
                // satisfy the zero-miss expectation for the wrong reason.
                Assert.Equal(20, reported.SampleSize);

                // Coverage follows whether the thread settled, not whether a miss was counted. The
                // always-acted-on classifier can qualify a thread that never resolved, and that pull request
                // still withholds recall: nothing established that the concern was accepted.
                var settles = moment != ResolutionMoment.NeverResolves;
                Assert.Equal(settles ? 20 : 0, reported.CoveredSampleSize);
                Assert.Equal(settles, reported.Metrics.Recall is not null);
            }
        }
    }

    [Fact]
    public async Task E9_TheConvergingSweepSealsTheHistoricalBacklog_WithoutClaimingARecallItCannotMeasure()
    {
        // The state the migration left behind: pull requests that closed long ago, whose harvested judgements
        // were all taken while their threads were open. The crawl no longer reaches them, so no judgement of
        // theirs can be revised, but the converging sweep can now reach them and seal them.
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

        for (long id = 1; id <= 80; id++)
        {
            await this.SeedPullRequestAsync(id, findings: 8, addressed: 7, humanThreads: 3, ResolutionMoment.NeverResolves, falsePositive: 1);
        }

        // One crawl pass harvests every thread while it is open, then every pull request closes. This is the
        // whole history in two cycles.
        await this.RunCyclesAsync(cycles: 1, closePullRequestOnCycle: null);
        await this.RunCyclesAsync(cycles: 1, closePullRequestOnCycle: 1);

        var harvested = await this._db.CodeInsightMisses.CountAsync();
        var qualifying = await this._db.CodeInsightMisses.CountAsync(miss => miss.CountsAsMiss);
        this.Line($"E9 harvested misses={harvested}, of which qualifying={qualifying}");

        // Now let the sweep run to convergence, the way it does after the fix.
        for (var sweep = 1; sweep <= 6; sweep++)
        {
            await this.RunCyclesAsync(cycles: 1, closePullRequestOnCycle: null);
            var snapshot = await this.ReadAsync();
            this.Line(
                $"  sweep {sweep}: seals={snapshot.SampleSize,3} TP={snapshot.Metrics.Inputs.TruePositives,3} "
                + $"FP={snapshot.Metrics.Inputs.FalsePositives,3} FN={snapshot.Metrics.Inputs.FalseNegatives,3} "
                + $"F1={Fmt(snapshot.Metrics.F1)}");
        }

        var final = await this.ReadAsync();
        this.Report("E9 after convergence", final);

        // The sweep still measures every one of them, and no later observation can add a false negative to a
        // sealed row. What changed is that a row whose threads never settled no longer reports the recall of
        // a reviewer that missed nothing: precision stands, recall is withheld.
        Assert.Equal(80, final.SampleSize);
        Assert.Equal(0, final.CoveredSampleSize);
        Assert.Equal(0, final.Metrics.Inputs.FalseNegatives);
        Assert.Null(final.Metrics.Recall);
        Assert.NotNull(final.Metrics.Precision);
    }

    [Fact]
    public async Task E11_TheCloseSettlesBothSidesOfTheRatio_SoRecallMatchesTheTruth()
    {
        // Both sides of the ratio settle at the same moment: the human threads the close resolves become
        // countable misses, and ProPR's own finding threads resolve too. Recording only the first would leave
        // the false negatives complete and the true positives short, which reads as a recall the reviewer did
        // not earn. Half the findings here have no outcome from any active pass.
        this.Build();
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

        const int pullRequests = 20;
        const int findingsEach = 8;
        const int resolvedAtClose = 4;
        const int humanThreadsEach = 3;

        for (long id = 1; id <= pullRequests; id++)
        {
            await this.SeedPullRequestAsync(
                id,
                findings: findingsEach,
                addressed: findingsEach,
                humanThreads: humanThreadsEach,
                ResolutionMoment.AtClose,
                findingsResolvedAtClose: resolvedAtClose);
        }

        await this.RunCyclesAsync(cycles: 25, closePullRequestOnCycle: 15);

        var reported = await this.ReadAsync();

        // Worked out from the seeded population by hand, not run through the calculator under test: an
        // expected value produced by the implementation would reproduce any formula regression it has.
        // 160 findings all addressed, 60 qualifying misses, no false positives.
        const int expectedTruePositives = pullRequests * findingsEach;
        const int expectedMisses = pullRequests * humanThreadsEach;
        const double expectedPrecision = 1d;
        const double expectedRecall = 160d / (160d + 60d);
        const double expectedF1 = 2d * expectedPrecision * expectedRecall / (expectedPrecision + expectedRecall);

        this.Report("E11 reported", reported);
        this.Line($"E11 expected: TP={expectedTruePositives} FN={expectedMisses} recall={expectedRecall:P2}");

        // Both sides complete: the misses the close resolved and the outcomes of the findings it resolved
        // alongside them. Before the close recorded dispositions this reported 80 true positives and a recall
        // 15.58 points under the truth.
        //
        // The sample is asserted first: matching totals could otherwise be reached by aggregating over fewer
        // rows whose counts happened to add up.
        Assert.Equal(pullRequests, reported.SampleSize);
        Assert.Equal(pullRequests, reported.CoveredSampleSize);
        Assert.Equal(expectedMisses, reported.Metrics.Inputs.FalseNegatives);
        Assert.Equal(expectedTruePositives, reported.Metrics.Inputs.TruePositives);
        Assert.Equal(0, reported.Metrics.Inputs.FalsePositives);
        Assert.Equal(expectedPrecision, reported.Metrics.Precision!.Value, 12);
        Assert.Equal(expectedRecall, reported.Metrics.Recall!.Value, 12);
        Assert.Equal(expectedF1, reported.Metrics.F1!.Value, 12);
    }

    // ---------------------------------------------------------------- the simulated crawl

    /// <summary>
    ///     One cycle is one crawl pass over every collected pull request, followed by one seal sweep. The real
    ///     crawl runs the sweep every six hours and a pass every few minutes; the ratio does not change what is
    ///     being observed here, which is whether a value can move at all.
    /// </summary>
    internal async Task RunCyclesAsync(int cycles, int? closePullRequestOnCycle, bool reportEachCycle = false)
    {
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var closingNow = closePullRequestOnCycle == cycle;

            foreach (var pr in this._prs.Values)
            {
                if (pr.ClosedOnCycle is not null)
                {
                    // Already closed on an earlier cycle. Its review jobs were cancelled then, so the crawl's
                    // disappearance check no longer reaches it and no further pass observes it.
                    continue;
                }

                if (closingNow)
                {
                    if (pr.Threads.Count > 0 && pr.Moment == ResolutionMoment.AtClose)
                    {
                        // The provider resolves the threads as part of completing the pull request. No Active
                        // pass will see this state, because the pull request is not Active again.
                        foreach (var thread in pr.Threads)
                        {
                            thread.Resolved = true;
                        }
                    }

                    pr.ClosedOnCycle = cycle;

                    // A crawl reaches lifecycle synchronization for a closing pull request only while one of
                    // its review jobs is still active. Every other close is left to the sweep below, which is
                    // the common case: the review finishes long before the pull request does.
                    if (pr.ReviewJobStillActiveAtClose)
                    {
                        this.LastLifecycleOutcome = await this._synchronization.SynchronizeAsync(
                            this.LifecycleRequest(pr),
                            CancellationToken.None);
                    }

                    continue;
                }

                if (pr.Moment == ResolutionMoment.WhileStillOpen && cycle >= pr.ResolveOnCycle)
                {
                    foreach (var thread in pr.Threads)
                    {
                        thread.Resolved = true;
                    }
                }

                // The Active path: every thread on the pull request is handed to the harvester on every pass.
                foreach (var thread in pr.Threads)
                {
                    await this._harvester.HandleThreadObservedAsync(this.Event(pr, thread), CancellationToken.None);
                }
            }

            // The sweep only looks at aggregates quiet for seven days. Nothing in this simulation can wait a
            // week, so a closed pull request's activity anchor is backdated once, which is exactly the state a
            // real aggregate reaches after a week of no collection activity.
            await this.BackdateClosedAggregatesAsync();

            await this._sweeper.SweepAsync(25, TimeSpan.FromDays(7), CancellationToken.None);

            if (reportEachCycle)
            {
                var snapshot = await this.ReadAsync();
                this.Line(
                    $"  cycle {cycle,2}: seals={snapshot.SampleSize,3} TP={snapshot.Metrics.Inputs.TruePositives,3} "
                    + $"FP={snapshot.Metrics.Inputs.FalsePositives,3} FN={snapshot.Metrics.Inputs.FalseNegatives,3} "
                    + $"P={Fmt(snapshot.Metrics.Precision)} R={Fmt(snapshot.Metrics.Recall)} F1={Fmt(snapshot.Metrics.F1)}");
            }
        }
    }

    private async Task BackdateClosedAggregatesAsync()
    {
        var closed = this._prs.Values.Where(pr => pr.ClosedOnCycle is not null).Select(pr => pr.Id).ToList();
        if (closed.Count == 0)
        {
            return;
        }

        var aggregates = await this._db.CodeInsightPullRequests
            .Where(row => closed.Contains(row.PullRequestId))
            .ToListAsync();

        var quiet = DateTimeOffset.UtcNow.AddDays(-30);
        foreach (var aggregate in aggregates.Where(row => row.LastActivityAt > quiet))
        {
            aggregate.LastActivityAt = quiet;
        }

        await this._db.SaveChangesAsync();
    }

    /// <summary>
    ///     Settles every thread and reopens every pull request, so later cycles observe them again. The reopen
    ///     is what lets the experiment ask whether a judgement revised after a seal can still reach it.
    /// </summary>
    private void ReopenWithEveryThreadResolved()
    {
        foreach (var thread in this._prs.Values.SelectMany(pr => pr.Threads))
        {
            thread.Resolved = true;
        }

        foreach (var pr in this._prs.Values)
        {
            pr.ClosedOnCycle = null;
        }
    }

    // ---------------------------------------------------------------- wiring

    internal void Build()
    {
        this._store = new CodeInsightFindingStore(this._db, CreateCodec());

        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this._classifier = Substitute.For<IHumanMissClassifier>();
        this._classifier.ClassifierVersion.Returns("simulation");
        this.WithClassifier(actedOnWhenResolved: true, actedOnWhenOpen: false);

        this._harvester = new CodeInsightMissHarvester(
            this._store,
            this._store,
            this._classifier,
            gate,
            NullLogger<CodeInsightMissHarvester>.Instance);

        this._sealer = new CodeInsightMetricSealer(
            this._db,
            gate,
            NullLogger<CodeInsightMetricSealer>.Instance);

        this._provider = Substitute.For<IPullRequestFetcher>();

        // Reads the seeded state on every call, so a pull request closed earlier in the same cycle already
        // reports as finished when the lifecycle pass asks. Assigning this per cycle instead would leave the
        // first cycle answering from a stale closure.
        this._providerStatus = id =>
            this._prs.TryGetValue(id, out var pr) && pr.ClosedOnCycle is not null
                ? PrStatus.Completed
                : PrStatus.Active;
        this._provider
            .FetchRefAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new PullRequestRef("feature", "main", this._providerStatus((int)call[3])));

        this._provider
            .FetchThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => this.ThreadsOf((int)call[3]));

        this._jobs = Substitute.For<IJobRepository>();
        // Answers about the pull request the job belongs to. A fixed identity would let production code that
        // reads the job's pull request act on the wrong one while the experiment still passed.
        this._jobs.GetById(Arg.Any<Guid>()).Returns(call =>
        {
            var jobId = (Guid)call[0];
            var owner = this._prs.Values.FirstOrDefault(pr => pr.ActiveJobId == jobId);
            return new ReviewJob(
                jobId,
                ClientId,
                "https://dev.azure.com/org",
                "project",
                Repository,
                (int)(owner?.Id ?? 1),
                1);
        });

        // A pull request seeded as closing while its review was still running is what takes the crawl into
        // lifecycle synchronization, and it is also the case with a job left to cancel.
        this._jobs.GetActiveJobsForConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => this._prs.Values
                .Where(pr => pr.ReviewJobStillActiveAtClose && !this._cancelledJobs.Contains(pr.ActiveJobId))
                .Select(pr => new ReviewJob(pr.ActiveJobId, ClientId, "https://dev.azure.com/org", "project", Repository, (int)pr.Id, 1))
                .ToList());

        // A cancelled job stops being active. Without this a later cycle sees it as still running and the
        // reconciliation is asked to cancel it again.
        this._jobs.SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                this._cancelledJobs.Add((Guid)call[0]);
                return Task.CompletedTask;
            });
        var jobs = this._jobs;

        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns([CreateConnection()]);

        var origins = Substitute.For<IPostedCommentOriginStore>();
        origins.GetJobIdsForPullRequestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns([]);

        this._dispositions = new CodeInsightDispositionService(
            this._store,
            this._store,
            Substitute.For<IDisregardedFindingClassifier>(),
            gate,
            NullLogger<CodeInsightDispositionService>.Instance);

        var reviewerThreads = Substitute.For<IReviewerThreadStatusFetcher>();
        reviewerThreads
            .GetReviewerThreadStatusesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(call => this.ReviewerThreadsOf((int)call[3]));

        this._closeObserver = new PullRequestCloseObserver(
            NullLogger<PullRequestCloseObserver>.Instance,
            this._provider,
            this._harvester,
            this._store,
            gate,
            connections,
            origins,
            reviewerThreads,
            this._dispositions);

        this._synchronization = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            codeInsightMetricSealer: this._sealer,
            codeInsightCloseObserver: this._closeObserver);

        this._sweeper = new CodeInsightSealSweeper(
            this._db,
            this._sealer,
            gate,
            jobs,
            NullLogger<CodeInsightSealSweeper>.Instance,
            this._provider,
            this._closeObserver);

        this._reader = new CodeInsightMetricReader(this._db, new CodeInsightRollupReader(this._db));
    }

    /// <summary>
    ///     The stub classifier answers the acted-on question from the thread's resolved state, which is the
    ///     strongest form of the behaviour the real prompt describes: an open thread cannot show an acceptance
    ///     or a fix, a resolved one can.
    /// </summary>
    internal void WithClassifier(bool actedOnWhenResolved, bool actedOnWhenOpen)
    {
        this._classifier
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = (HumanMissJudgementRequest)call[0];
                var actedOn = request.ThreadResolved ? actedOnWhenResolved : actedOnWhenOpen;
                return Task.FromResult<HumanMissJudgement?>(new HumanMissJudgement(true, actedOn, true, 0.9, "simulated"));
            });
    }

    // ---------------------------------------------------------------- seeding

    private readonly Dictionary<long, SimulatedPullRequest> _prs = [];

    internal async Task SeedPullRequestAsync(
        long pullRequestId,
        int findings,
        int addressed,
        int humanThreads,
        ResolutionMoment moment,
        int falsePositive = 0,
        bool reviewJobStillActiveAtClose = false,
        int findingsResolvedAtClose = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(findingsResolvedAtClose);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(findingsResolvedAtClose, findings);

        var jobId = Guid.NewGuid();
        var key = this.Key(pullRequestId);
        List<string> resolvedAtCloseThreads = [];

        if (findings > 0)
        {
            var snapshots = Enumerable.Range(0, findings)
                .Select(ordinal => new CodeInsightFindingSnapshot(
                    ordinal,
                    $"src/File{ordinal}.cs",
                    100 + ordinal,
                    CommentSeverity.Error,
                    $"Simulated finding {ordinal} about an unrelated concern number {ordinal}.",
                    "Baseline",
                    null,
                    null,
                    false,
                    ReviewCommentScopeRelation.OnChangedLine,
                    null,
                    $"propr-thread-{pullRequestId}-{ordinal}",
                    $"propr-comment-{pullRequestId}-{ordinal}"))
                .ToList();

            await this._store.MaterialiseFindingsAsync(key, jobId, $"rev-{pullRequestId}", DateTimeOffset.UtcNow, snapshots);

            var stored = await this._store.GetFindingsForPullRequestAsync(key);
            var index = 0;
            foreach (var finding in stored)
            {
                if (index >= findings - findingsResolvedAtClose)
                {
                    // This finding's own thread resolves as part of the close, so no active pass ever recorded
                    // an outcome for it. Whether the seal counts it is what the close observation decides.
                    resolvedAtCloseThreads.Add(finding.ProviderThreadId!);
                    index++;
                    continue;
                }

                var disposition = index < addressed
                    ? CodeInsightDisposition.Addressed
                    : index < addressed + falsePositive
                        ? CodeInsightDisposition.FalsePositive
                        : CodeInsightDisposition.Acknowledged;

                await this._store.RecordDispositionAsync(
                    finding.Id,
                    new CodeInsightDispositionRecord(
                        disposition,
                        ThreadResolutionIntent.ClaimsFix,
                        ThreadAnchorCodeChange.Changed,
                        "simulation",
                        0.9));
                index++;
            }
        }

        this._prs[pullRequestId] = new SimulatedPullRequest(pullRequestId, moment, reviewJobStillActiveAtClose)
        {
            FindingThreadsResolvedAtClose = resolvedAtCloseThreads,
            Threads = Enumerable.Range(0, humanThreads)
                .Select(i => new SimulatedThread($"human-thread-{pullRequestId}-{i}", i))
                .ToList(),
        };
    }

    private ThreadUpdatedEvent Event(SimulatedPullRequest pr, SimulatedThread thread)
    {
        var status = thread.Resolved ? "Fixed" : "Active";
        return new ThreadUpdatedEvent(
            ClientId,
            Guid.NewGuid(),
            Repository,
            pr.Id,
            thread.Id,
            $"src/Human{thread.Ordinal}.cs",
            200 + thread.Ordinal,
            status,
            DateTimeOffset.UtcNow,
            [
                new ThreadUpdatedComment(
                    $"{thread.Id}-c1",
                    "alice",
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    $"This retry loop swallows the cancellation token on path {thread.Ordinal}, so a shutdown hangs."),
                new ThreadUpdatedComment(
                    $"{thread.Id}-c2",
                    "bob",
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    "Good catch, fixed."),
            ]);
    }

    /// <summary>
    ///     The threads ProPR itself raised, as the status read would report them at the close. A finding seeded
    ///     as resolving at the close has no outcome recorded by any active pass.
    /// </summary>
    private IReadOnlyList<PrThreadStatusEntry> ReviewerThreadsOf(long pullRequestId)
    {
        if (!this._prs.TryGetValue(pullRequestId, out var pr) || pr.ClosedOnCycle is null)
        {
            return [];
        }

        return pr.FindingThreadsResolvedAtClose
            .Select(threadId => new PrThreadStatusEntry(
                threadId,
                "Fixed",
                "src/File.cs",
                "propr: A finding ProPR raised.",
                0,
                ThreadAnchorCodeChange.Changed))
            .ToList();
    }

    /// <summary>The threads the provider would return for one pull request, in their current state.</summary>
    private IReadOnlyList<PrCommentThread> ThreadsOf(long pullRequestId)
    {
        if (!this._prs.TryGetValue(pullRequestId, out var pr))
        {
            return [];
        }

        return pr.Threads
            .Select(thread => new PrCommentThread(
                thread.Id,
                $"src/Human{thread.Ordinal}.cs",
                200 + thread.Ordinal,
                [
                    new PrThreadComment(
                        "Jane Dev",
                        $"This retry loop swallows the cancellation token on path {thread.Ordinal}, so a shutdown hangs.",
                        HumanAuthorId,
                        100 + thread.Ordinal,
                        DateTimeOffset.UtcNow.AddMinutes(-10)),
                    new PrThreadComment(
                        "Sam Reviewer",
                        "Good catch, fixed.",
                        HumanAuthorId,
                        200 + thread.Ordinal,
                        DateTimeOffset.UtcNow.AddMinutes(-5)),
                ],
                thread.Resolved ? "Fixed" : "Active"))
            .ToList();
    }

    private PullRequestSynchronizationRequest LifecycleRequest(SimulatedPullRequest pr)
    {
        return new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Crawl,
            SummaryLabel = "crawl disappearance",
            ClientId = ClientId,
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "project",
            RepositoryId = Repository,
            PullRequestId = (int)pr.Id,
            PullRequestStatus = PrStatus.Completed,
            Provider = ScmProvider.AzureDevOps,
            AllowReviewSubmission = false,
        };
    }

    private static ClientScmConnectionDto CreateConnection()
    {
        var now = DateTimeOffset.UtcNow;
        return new ClientScmConnectionDto(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ClientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            ScmAuthenticationKind.PersonalAccessToken,
            "AzureDevOps",
            true,
            "verified",
            now,
            null,
            null,
            now,
            now);
    }

    private CodeInsightPullRequestKey Key(long pullRequestId)
    {
        return new CodeInsightPullRequestKey(ClientId, Repository, pullRequestId);
    }

    // ---------------------------------------------------------------- reporting

    internal Task<CodeInsightMetricResult> ReadAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return this._reader.GetCorrectnessAsync(new CodeInsightRollupQuery([ClientId], today.AddDays(-400), today.AddDays(1)));
    }

    private void Report(string label, CodeInsightMetricResult result)
    {
        var inputs = result.Metrics.Inputs;
        this.Line(
            $"{label}: seals={result.SampleSize} TP={inputs.TruePositives} FP={inputs.FalsePositives} "
            + $"FN={inputs.FalseNegatives} | precision={Fmt(result.Metrics.Precision)} "
            + $"recall={Fmt(result.Metrics.Recall)} F1={Fmt(result.Metrics.F1)}");
    }

    private static string Fmt(double? value)
    {
        return value is null ? "—" : value.Value.ToString("P2");
    }

    private void Line(string line)
    {
        this._log.Add(line);
        output.WriteLine(line);
    }

    public void Dispose()
    {
        this._db.Dispose();

        foreach (var provider in this._codecProviders)
        {
            provider.Dispose();
        }

        foreach (var directory in this._keyDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover key directory under the temp path costs nothing and is not worth failing a run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static MeisterProPRDbContext NewContext()
    {
        return new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase($"F1RecoverySimulation-{Guid.NewGuid():N}")
                .Options);
    }

    private ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterDev.ProPR.F1Sim.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);
        this._keyDirectories.Add(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        this._codecProviders.Add(provider);

        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private sealed class SimulatedPullRequest(long id, ResolutionMoment moment, bool reviewJobStillActiveAtClose)
    {
        public long Id { get; } = id;

        public ResolutionMoment Moment { get; } = moment;

        public bool ReviewJobStillActiveAtClose { get; } = reviewJobStillActiveAtClose;

        public Guid ActiveJobId { get; } = Guid.NewGuid();

        public int ResolveOnCycle { get; } = 5;

        public int? ClosedOnCycle { get; set; }

        public List<SimulatedThread> Threads { get; init; } = [];

        /// <summary>Provider thread ids of ProPR's own findings whose threads the close resolves.</summary>
        public List<string> FindingThreadsResolvedAtClose { get; init; } = [];
    }

    private sealed class SimulatedThread(string id, int ordinal)
    {
        public string Id { get; } = id;

        public int Ordinal { get; } = ordinal;

        public bool Resolved { get; set; }
    }
}
