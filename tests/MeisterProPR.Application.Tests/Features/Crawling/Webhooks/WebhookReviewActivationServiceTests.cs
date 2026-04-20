// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Services;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Webhooks;

public sealed class WebhookReviewActivationServiceTests
{
    [Fact]
    public async Task ActivateAsync_CreatedEvent_SubmitsReviewJobForLatestIteration()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateDelivery();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var intakeStore = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var createdJob = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            7);

        iterationResolver.GetLatestIterationIdAsync(
                configuration.ClientId,
                configuration.OrganizationUrl,
                configuration.ProjectId,
                delivery.RepositoryId,
                delivery.PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(7);
        intakeStore.FindActiveJobAsync(
                configuration.ClientId,
                Arg.Is<SubmitReviewJobRequestDto>(request =>
                    request.ProviderScopePath == configuration.OrganizationUrl
                    && request.ProviderProjectKey == configuration.ProjectId
                    && request.RepositoryId == delivery.RepositoryId
                    && request.PullRequestId == delivery.PullRequestId
                    && request.IterationId == 7),
                Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        intakeStore.CreatePendingJobAsync(
                configuration.ClientId,
                Arg.Is<SubmitReviewJobRequestDto>(request =>
                    request.ProviderScopePath == configuration.OrganizationUrl &&
                    request.ProviderProjectKey == configuration.ProjectId &&
                    request.RepositoryId == delivery.RepositoryId &&
                    request.PullRequestId == delivery.PullRequestId &&
                    request.IterationId == 7),
                Arg.Any<CancellationToken>())
            .Returns(createdJob);

        var submitHandler = new SubmitReviewJobHandler(intakeStore, queue, NullLogger<SubmitReviewJobHandler>.Instance);
        var sut = new WebhookReviewActivationService(
            iterationResolver,
            submitHandler,
            NullLogger<WebhookReviewActivationService>.Instance);

        var actionSummaries = await sut.ActivateAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.PullRequestCreated),
            CancellationToken.None);

        await iterationResolver.Received(1)
            .GetLatestIterationIdAsync(
                configuration.ClientId,
                configuration.OrganizationUrl,
                configuration.ProjectId,
                delivery.RepositoryId,
                delivery.PullRequestId,
                Arg.Any<CancellationToken>());
        await intakeStore.Received(1)
            .CreatePendingJobAsync(
                configuration.ClientId,
                Arg.Any<SubmitReviewJobRequestDto>(),
                Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(createdJob.Id, Arg.Any<CancellationToken>());
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("Submitted review intake job", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("pull request created", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActivateAsync_DuplicateSubmission_ReturnsDuplicateActionSummary()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateDelivery();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var intakeStore = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var existingJob = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            7)
        {
            Status = JobStatus.Pending,
        };

        iterationResolver.GetLatestIterationIdAsync(
                configuration.ClientId,
                configuration.OrganizationUrl,
                configuration.ProjectId,
                delivery.RepositoryId,
                delivery.PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(7);
        intakeStore.FindActiveJobAsync(
                configuration.ClientId,
                Arg.Is<SubmitReviewJobRequestDto>(request =>
                    request.ProviderScopePath == configuration.OrganizationUrl
                    && request.ProviderProjectKey == configuration.ProjectId
                    && request.RepositoryId == delivery.RepositoryId
                    && request.PullRequestId == delivery.PullRequestId
                    && request.IterationId == 7),
                Arg.Any<CancellationToken>())
            .Returns(existingJob);

        var submitHandler = new SubmitReviewJobHandler(intakeStore, queue, NullLogger<SubmitReviewJobHandler>.Instance);
        var sut = new WebhookReviewActivationService(
            iterationResolver,
            submitHandler,
            NullLogger<WebhookReviewActivationService>.Instance);

        var actionSummaries = await sut.ActivateAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.PullRequestUpdated),
            CancellationToken.None);

        await intakeStore.DidNotReceive()
            .CreatePendingJobAsync(Arg.Any<Guid>(), Arg.Any<SubmitReviewJobRequestDto>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("Skipped duplicate active job", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("pull request updated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActivateAsync_ReviewerAssignment_UsesReviewerAssignmentActionSummary()
    {
        var configuration = CreateConfiguration();
        var delivery = CreateDelivery([Guid.Parse("11111111-1111-1111-1111-111111111111")]);
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var intakeStore = Substitute.For<IReviewJobIntakeStore>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var createdJob = new ReviewJob(
            Guid.NewGuid(),
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            9);

        iterationResolver.GetLatestIterationIdAsync(
                configuration.ClientId,
                configuration.OrganizationUrl,
                configuration.ProjectId,
                delivery.RepositoryId,
                delivery.PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(9);
        intakeStore.FindActiveJobAsync(
                configuration.ClientId,
                Arg.Is<SubmitReviewJobRequestDto>(request =>
                    request.ProviderScopePath == configuration.OrganizationUrl
                    && request.ProviderProjectKey == configuration.ProjectId
                    && request.RepositoryId == delivery.RepositoryId
                    && request.PullRequestId == delivery.PullRequestId
                    && request.IterationId == 9),
                Arg.Any<CancellationToken>())
            .Returns((ReviewJob?)null);
        intakeStore.CreatePendingJobAsync(
                configuration.ClientId,
                Arg.Any<SubmitReviewJobRequestDto>(),
                Arg.Any<CancellationToken>())
            .Returns(createdJob);

        var submitHandler = new SubmitReviewJobHandler(intakeStore, queue, NullLogger<SubmitReviewJobHandler>.Instance);
        var sut = new WebhookReviewActivationService(
            iterationResolver,
            submitHandler,
            NullLogger<WebhookReviewActivationService>.Instance);

        var actionSummaries = await sut.ActivateAsync(
            configuration,
            delivery,
            new AdoWebhookEventClassification(AdoWebhookEventKind.ReviewerAssigned),
            CancellationToken.None);

        Assert.Contains(
            actionSummaries,
            summary => summary.Contains("reviewer assignment", StringComparison.OrdinalIgnoreCase));
        await queue.Received(1).EnqueueAsync(createdJob.Id, Arg.Any<CancellationToken>());
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
            [
                WebhookEventType.PullRequestCreated, WebhookEventType.PullRequestUpdated,
                WebhookEventType.PullRequestCommented,
            ],
            [],
            SecretCiphertext: "ciphertext");
    }

    private static IncomingAdoWebhookDelivery CreateDelivery(IReadOnlyList<Guid>? reviewerIds = null)
    {
        return new IncomingAdoWebhookDelivery(
            "path-key",
            "git.pullrequest.updated",
            WebhookEventType.PullRequestUpdated,
            "repo-1",
            42,
            "refs/heads/feature/test",
            "refs/heads/main",
            "active",
            reviewerIds ?? []);
    }
}
