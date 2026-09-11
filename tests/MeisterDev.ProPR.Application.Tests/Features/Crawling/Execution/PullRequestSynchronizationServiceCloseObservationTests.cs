// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
///     Lifecycle synchronization observes the pull request's threads once before it seals. Every earlier pass
///     ran while the pull request was active, so a thread that reached its resolved state as part of the close
///     has never been seen in that state, and the seal counts only judgements already recorded.
/// </summary>
public sealed class PullRequestSynchronizationServiceCloseObservationTests
{
    private static readonly Guid ClientId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Theory]
    [InlineData(PrStatus.Completed)]
    [InlineData(PrStatus.Abandoned)]
    public async Task TheThreadsAreObservedBeforeTheMeasurementIsSealed(PrStatus status)
    {
        // A judgement revised after the seal is written can never be counted: the seal is taken once and the
        // row it writes never moves.
        var harness = new Harness();

        await harness.RunAsync(status);

        Received.InOrder(() =>
        {
            harness.Observer.ObserveAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            harness.Sealer.SealAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        });

        // Ordering alone would also hold if the pass observed or sealed twice.
        await harness.Observer.Received(1).ObserveAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.Sealer.Received(1).SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheObservationIsAddressedToThePullRequestThatClosed()
    {
        var harness = new Harness();

        await harness.RunAsync(PrStatus.Completed);

        await harness.Observer.Received(1).ObserveAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == ClientId && key.RepositoryId == "repo-1" && key.PullRequestId == 42),
            "https://dev.azure.com/org",
            "project",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnActivePullRequestIsNeitherObservedNorSealed()
    {
        // The active pass has its own thread observation, which archives as well. This one is for the close.
        var harness = new Harness();

        await harness.RunAsync(PrStatus.Active);

        await harness.Observer.DidNotReceive().ObserveAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.Sealer.DidNotReceive().SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAnObserverTheCloseStillSeals()
    {
        var harness = new Harness(withObserver: false);

        await harness.RunAsync(PrStatus.Completed);

        await harness.Sealer.Received(1).SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            "Completed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingObservationLeavesTheSealAndTheJobCancellationIntact()
    {
        // Cancelling the superseded jobs of a closed pull request is real work with real cost attached, and the
        // measurement is the reason this pass exists. Neither may be lost to a failed observation.
        var harness = new Harness();
        harness.Observer
            .ObserveAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the provider is unreachable"));
        harness.WithActiveJob();

        var outcome = await harness.RunAsync(PrStatus.Abandoned);

        await harness.Observer.Received(1).ObserveAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.Sealer.Received(1).SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            "Abandoned",
            Arg.Any<CancellationToken>());
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        await harness.Jobs.Received(1).SetCancelledAsync(harness.ActiveJobId, Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly PullRequestSynchronizationService _sut;

        public Harness(bool withObserver = true)
        {
            this.Sealer = Substitute.For<ICodeInsightMetricSealer>();
            this.Observer = Substitute.For<ICodeInsightCloseObserver>();
            this.Jobs = Substitute.For<IJobRepository>();

            this.Jobs.GetActiveJobsForConfigAsync(
                    "https://dev.azure.com/org",
                    "project",
                    Arg.Any<CancellationToken>())
                .Returns([]);

            this._sut = new PullRequestSynchronizationService(
                this.Jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                codeInsightMetricSealer: this.Sealer,
                codeInsightCloseObserver: withObserver ? this.Observer : null);
        }

        public ICodeInsightMetricSealer Sealer { get; }

        public ICodeInsightCloseObserver Observer { get; }

        public IJobRepository Jobs { get; }

        public Guid ActiveJobId { get; } = Guid.NewGuid();

        public void WithActiveJob()
        {
            var job = new ReviewJob(
                this.ActiveJobId,
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                7);

            this.Jobs.GetActiveJobsForConfigAsync(
                    "https://dev.azure.com/org",
                    "project",
                    Arg.Any<CancellationToken>())
                .Returns([job]);
        }

        public Task<PullRequestSynchronizationOutcome> RunAsync(PrStatus status)
        {
            return this._sut.SynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl disappearance",
                    ClientId = ClientId,
                    ProviderScopePath = "https://dev.azure.com/org",
                    ProviderProjectKey = "project",
                    RepositoryId = "repo-1",
                    PullRequestId = 42,
                    PullRequestStatus = status,
                    Provider = ScmProvider.AzureDevOps,
                    AllowReviewSubmission = false,
                });
        }
    }
}
