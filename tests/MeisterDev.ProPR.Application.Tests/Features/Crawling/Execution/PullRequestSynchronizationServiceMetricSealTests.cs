// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
///     The correctness measurement is taken at the same moment the pull request stops being active, on the
///     shared synchronization path both the crawl and the webhooks go through, so one hook covers merged,
///     abandoned, and closed.
/// </summary>
public sealed class PullRequestSynchronizationServiceMetricSealTests
{
    private static readonly Guid ClientId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Theory]
    [InlineData(PrStatus.Completed, "Completed")]
    [InlineData(PrStatus.Abandoned, "Abandoned")]
    public async Task AFinishedPullRequestIsSealedWithTheStateThatWasObserved(PrStatus status, string expectedState)
    {
        var harness = new Harness();

        await harness.RunAsync(status);

        await harness.Sealer.Received(1).SealAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == ClientId
                && key.RepositoryId == "repo-1"
                && key.PullRequestId == 42),
            expectedState,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnActivePullRequestIsNotSealed()
    {
        // The measurement is only meaningful once the pull request has stopped receiving outcomes.
        var harness = new Harness();

        await harness.RunAsync(PrStatus.Active);

        await harness.Sealer.DidNotReceive().SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingSealDoesNotStopTheLifecycleWorkThatMatters()
    {
        // Cancelling the superseded jobs of a closed pull request is real work with real cost attached. A
        // measurement is not allowed to get in its way.
        var harness = new Harness();
        harness.Sealer
            .SealAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is unreachable"));
        harness.WithActiveJob();

        var outcome = await harness.RunAsync(PrStatus.Abandoned);

        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        await harness.Jobs.Received(1).SetCancelledAsync(harness.ActiveJobId, Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly PullRequestSynchronizationService _sut;

        public Harness()
        {
            this.Jobs = Substitute.For<IJobRepository>();
            this.Sealer = Substitute.For<ICodeInsightMetricSealer>();

            this.Jobs.GetActiveJobsForConfigAsync(
                    "https://dev.azure.com/org",
                    "project",
                    Arg.Any<CancellationToken>())
                .Returns([]);

            this._sut = new PullRequestSynchronizationService(
                this.Jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                codeInsightMetricSealer: this.Sealer);
        }

        public IJobRepository Jobs { get; }

        public ICodeInsightMetricSealer Sealer { get; }

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

                    // An active pull request reaches this path from the crawl's disappearance check, which
                    // deliberately queues no review work.
                    AllowReviewSubmission = false,
                });
        }
    }
}
