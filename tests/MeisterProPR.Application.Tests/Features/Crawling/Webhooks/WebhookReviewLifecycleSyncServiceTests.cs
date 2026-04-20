// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Webhooks;

public sealed class WebhookReviewLifecycleSyncServiceTests
{
    [Fact]
    public async Task SynchronizeAsync_ClosedPullRequest_CancelsMatchingActiveJobs()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateClosedDelivery();
        var jobRepository = Substitute.For<IJobRepository>();
        var matchingPending = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            3)
        {
            Status = JobStatus.Pending,
        };
        var matchingProcessing = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            4)
        {
            Status = JobStatus.Processing,
        };
        var unrelated = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId + 1,
            4)
        {
            Status = JobStatus.Pending,
        };

        jobRepository.GetActiveJobsForConfigAsync(
                configuration.OrganizationUrl,
                configuration.ProjectId,
                Arg.Any<CancellationToken>())
            .Returns([matchingPending, matchingProcessing, unrelated]);

        var sut = new WebhookReviewLifecycleSyncService(
            jobRepository,
            NullLogger<WebhookReviewLifecycleSyncService>.Instance);

        var actionSummaries = await sut.SynchronizeAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.PullRequestClosed),
            CancellationToken.None);

        await jobRepository.Received(1).SetCancelledAsync(matchingPending.Id, Arg.Any<CancellationToken>());
        await jobRepository.Received(1).SetCancelledAsync(matchingProcessing.Id, Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceive().SetCancelledAsync(unrelated.Id, Arg.Any<CancellationToken>());
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("Cancelled 2 active review job(s)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SynchronizeAsync_NoMatchingActiveJobs_ReturnsNoOpSummary()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateClosedDelivery();
        var jobRepository = Substitute.For<IJobRepository>();
        var unrelated = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId + 7,
            5)
        {
            Status = JobStatus.Pending,
        };

        jobRepository.GetActiveJobsForConfigAsync(
                configuration.OrganizationUrl,
                configuration.ProjectId,
                Arg.Any<CancellationToken>())
            .Returns([unrelated]);

        var sut = new WebhookReviewLifecycleSyncService(
            jobRepository,
            NullLogger<WebhookReviewLifecycleSyncService>.Instance);

        var actionSummaries = await sut.SynchronizeAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.PullRequestClosed),
            CancellationToken.None);

        await jobRepository.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains(
                "No active review jobs required cancellation",
                StringComparison.OrdinalIgnoreCase));
    }

    private static WebhookConfigurationDto CreateConfiguration()
    {
        return new WebhookConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WebhookProviderType.AzureDevOps,
            "path-key",
            "https://dev.azure.com/org",
            "project",
            true,
            DateTimeOffset.UtcNow,
            [WebhookEventType.PullRequestUpdated],
            [],
            SecretCiphertext: "ciphertext");
    }

    private static IncomingAdoWebhookDelivery CreateClosedDelivery()
    {
        return new IncomingAdoWebhookDelivery(
            "path-key",
            "git.pullrequest.updated",
            WebhookEventType.PullRequestUpdated,
            "repo-1",
            42,
            "refs/heads/feature/test",
            "refs/heads/main",
            "abandoned",
            []);
    }
}
