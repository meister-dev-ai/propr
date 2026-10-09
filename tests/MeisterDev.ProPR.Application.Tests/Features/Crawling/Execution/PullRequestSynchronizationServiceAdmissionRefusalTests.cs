// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
/// What an automatic trigger does with a pull request head that review admission already refused. The
///     client in these tests reviews every increment. In that setting the refusal is the last check between
///     the trigger and a new job for an unchanged head.
/// </summary>
public sealed class PullRequestSynchronizationServiceAdmissionRefusalTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly ReviewAdmissionPolicy Policy = new(MaxRepositoryMegabytes: 5);

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_WhenAdmissionRefusedThisHeadUnderTheSameLimits_QueuesNothing(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = CreateJobs(refusedRevisionKey: "revision-a", refusedFingerprint: Policy.Fingerprint);
        var sut = CreateService(jobs, Policy);

        var outcome = await sut.SynchronizeAsync(CreateRequest(activationSource, summaryLabel, "revision-a"));

        Assert.Equal(PullRequestSynchronizationReviewDecision.AdmissionRefusedAtThisRevision, outcome.ReviewDecision);
        await jobs.DidNotReceiveWithAnyArgs().TryAddIfNoActiveDuplicateAsync(default!, default);
    }

    // An administrator who raises a bound changes what the head would be measured against, so the head is
    // measured again without waiting for a new commit.
    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_WhenTheAdmissionLimitsChangedSinceTheRefusal_QueuesTheReviewAgain(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = CreateJobs(refusedRevisionKey: "revision-a", refusedFingerprint: Policy.Fingerprint);
        var sut = CreateService(jobs, new ReviewAdmissionPolicy(MaxRepositoryMegabytes: 64));

        var outcome = await sut.SynchronizeAsync(CreateRequest(activationSource, summaryLabel, "revision-a"));

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_WhenTheHeadChangedSinceTheRefusal_QueuesTheReview(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = CreateJobs(refusedRevisionKey: "revision-a", refusedFingerprint: Policy.Fingerprint);
        var sut = CreateService(jobs, Policy);

        var outcome = await sut.SynchronizeAsync(CreateRequest(activationSource, summaryLabel, "revision-b"));

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    private static IJobRepository CreateJobs(string refusedRevisionKey, string? refusedFingerprint)
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetActiveJobsForConfigAsync(ClientId, "https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));
        jobs.GetLatestRefusedAdmissionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(new RefusedReviewAdmission(refusedRevisionKey, refusedFingerprint));
        return jobs;
    }

    private static PullRequestSynchronizationService CreateService(IJobRepository jobs, ReviewAdmissionPolicy policy)
    {
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(true);
        clientRegistry.GetReviewAdmissionPolicyAsync(ClientId, Arg.Any<CancellationToken>()).Returns(policy);

        return new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);
    }

    private static PullRequestSynchronizationRequest CreateRequest(
        PullRequestActivationSource activationSource,
        string summaryLabel,
        string providerRevisionId)
    {
        return new PullRequestSynchronizationRequest
        {
            ActivationSource = activationSource,
            SummaryLabel = summaryLabel,
            ClientId = ClientId,
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "project",
            RepositoryId = "repo-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
            CandidateIterationId = 7,
            ReviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", providerRevisionId, "patch-identity"),
            Provider = MeisterDev.ProPR.Domain.Enums.ScmProvider.AzureDevOps,
        };
    }
}
