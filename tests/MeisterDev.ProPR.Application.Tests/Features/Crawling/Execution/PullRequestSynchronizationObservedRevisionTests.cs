// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationObservedRevisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveObservationUsesCurrentRevisionForIntakeAndPreservesItsExistingJob(bool prepare, bool existing)
    {
        var current = new ReviewRevision("head-2", "base", null, "2", null);
        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Webhook, SummaryLabel = "webhook", ClientId = Guid.NewGuid(),
            Provider = ScmProvider.AzureDevOps, ProviderScopePath = "https://dev.azure.com/org", ProviderProjectKey = "project",
            RepositoryId = "repo", PullRequestId = 42, PullRequestStatus = PrStatus.Active, CandidateIterationId = 1,
            ReviewRevision = new ReviewRevision("head-1", "base", null, "1", null),
        };
        var discussions = Substitute.For<IDiscussionSynchronizationService>();
        discussions.ObserveAsync(request with { DeferMemoryProcessing = true }, Arg.Any<CancellationToken>()).Returns(
            new PullRequestThreadSnapshot(DateTimeOffset.UtcNow, [], [])
            {
                ObservedPullRequestStatus = PrStatus.Active, ObservedReviewRevision = current,
            });
        var jobs = Substitute.For<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, request.RepositoryId, 42, 2);
        job.SetReviewRevision(current);
        jobs.GetActiveJobsForConfigAsync(request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, Arg.Any<CancellationToken>())
            .Returns(existing ? new[] { job } : []);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(new TryAddReviewJobResult(true, null, 0));
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs,
            NullLogger<PullRequestSynchronizationService>.Instance, discussions: discussions);
        if (prepare)
        {
            await (await service.PrepareAsync(request)).CompleteAsync();
        }
        else
        {
            await service.SynchronizeAsync(request);
        }

        await jobs.DidNotReceive().SetSupersededAsync(job.Id, Arg.Any<CancellationToken>());
        if (existing)
        {
            await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(created => created.IterationId == 2 && created.ReviewRevisionReference == current), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task UnacknowledgedCloseEvidenceDefersSealingAndStillCancelsJobs()
    {
        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "close", ClientId = Guid.NewGuid(),
            Provider = ScmProvider.AzureDevOps, ProviderScopePath = "https://dev.azure.com/org", ProviderProjectKey = "project",
            RepositoryId = "repo", PullRequestId = 42, PullRequestStatus = PrStatus.Completed,
        };
        var jobs = Substitute.For<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, request.RepositoryId, 42, 1);
        jobs.GetActiveJobsForConfigAsync(request.ClientId, request.ProviderScopePath, request.ProviderProjectKey, Arg.Any<CancellationToken>()).Returns([job]);
        var observer = Substitute.For<MeisterDev.ProPR.CodeInsights.Contracts.ICodeInsightAcknowledgedCloseObserver>();
        observer.ObserveAcknowledgedAsync(
                Arg.Any<MeisterDev.ProPR.CodeInsights.Contracts.CodeInsightPullRequestKey>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MeisterDev.ProPR.CodeInsights.Contracts.CodeInsightEvidenceAcknowledgment(false));
        var sealer = Substitute.For<MeisterDev.ProPR.CodeInsights.Contracts.ICodeInsightMetricSealer>();
        var service = new PullRequestSynchronizationService(
            LocalScmPolicies.Registry, jobs,
            NullLogger<PullRequestSynchronizationService>.Instance, codeInsightCloseObserver: observer, codeInsightMetricSealer: sealer);
        await service.SynchronizeAsync(request);
        Assert.Empty(sealer.ReceivedCalls());
        await jobs.Received(1).SetCancelledAsync(job.Id, Arg.Any<CancellationToken>());
    }
}
