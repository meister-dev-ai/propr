// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Webhooks;

public sealed class ProviderWebhookLifecycleTests
{
    [Fact]
    public async Task SynchronizeAsync_CancelsMatchingJobsEvenWhenTheyCarryGitHubReviewContext()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateClosedDelivery();
        var jobRepository = Substitute.For<IJobRepository>();
        var matching = CreateGitHubJob(configuration.ClientId, delivery.RepositoryId, delivery.PullRequestId, 3);
        var unrelated = CreateGitHubJob(configuration.ClientId, delivery.RepositoryId, delivery.PullRequestId + 1, 4);

        jobRepository.GetActiveJobsForConfigAsync(
                configuration.OrganizationUrl,
                configuration.ProjectId,
                Arg.Any<CancellationToken>())
            .Returns([matching, unrelated]);

        var sut = new WebhookReviewLifecycleSyncService(
            jobRepository,
            NullLogger<WebhookReviewLifecycleSyncService>.Instance);

        var actionSummaries = await sut.SynchronizeAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.PullRequestClosed),
            CancellationToken.None);

        await jobRepository.Received(1).SetCancelledAsync(matching.Id, Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceive().SetCancelledAsync(unrelated.Id, Arg.Any<CancellationToken>());
        Assert.Equal(ScmProvider.GitHub, matching.Provider);
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("Cancelled 1 active review job", StringComparison.OrdinalIgnoreCase));
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
            "repo-gh-1",
            42,
            "refs/heads/feature/provider-neutral",
            "refs/heads/main",
            "closed",
            []);
    }

    private static ReviewJob CreateGitHubJob(Guid clientId, string repositoryId, int pullRequestId, int iterationId)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, repositoryId, "acme", "acme/propr");
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            pullRequestId.ToString(),
            pullRequestId);

        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            "https://dev.azure.com/org",
            "project",
            repositoryId,
            pullRequestId,
            iterationId);

        job.SetProviderReviewContext(review);
        return job;
    }
}
