// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationServiceDiscussionRefreshTests
{
    [Fact]
    public async Task ADiscussionThatCannotBeReadIsReportedAsUnavailable()
    {
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The selected connection is unavailable for this review target."));

        var outcome = await Service(discussions).SynchronizeAsync(Request());

        Assert.True(outcome.DiscussionUnavailable);
        Assert.Equal(PullRequestSynchronizationReviewDecision.None, outcome.ReviewDecision);
    }

    [Fact]
    public async Task ADiscussionWithoutACurrentRevisionIsNotReportedAsUnavailable()
    {
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [], false) { ObservedPullRequestStatus = PrStatus.Active });

        var outcome = await Service(discussions).SynchronizeAsync(Request());

        Assert.False(outcome.DiscussionUnavailable);
        Assert.Equal(PullRequestSynchronizationReviewDecision.None, outcome.ReviewDecision);
    }

    private static PullRequestSynchronizationService Service(IDiscussionSynchronizationService discussions) => new(
        MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute(),
        Substitute.For<IJobRepository>(),
        NullLogger<PullRequestSynchronizationService>.Instance,
        Substitute.For<IPullRequestIterationResolver>(),
        Substitute.For<IReviewerThreadStatusFetcher>(),
        Substitute.For<IThreadMemoryService>(),
        Substitute.For<IReviewPrScanRepository>(),
        Substitute.For<IClientRegistry>(),
        discussions: discussions);

    private static PullRequestSynchronizationRequest Request() => new()
    {
        Provider = ScmProvider.GitHub,
        ActivationSource = PullRequestActivationSource.Crawl,
        SummaryLabel = "discussion refresh",
        ClientId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        ProviderScopePath = "https://github.com",
        ProviderProjectKey = "acme",
        RepositoryId = "propr",
        PullRequestId = 2,
        PullRequestStatus = PrStatus.Active,
        AllowReviewSubmission = false,
        DiscussionOnly = true,
    };
}
