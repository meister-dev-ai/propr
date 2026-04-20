// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Services;

/// <summary>Reuses Reviewing.Intake to activate review work from webhook deliveries.</summary>
public sealed partial class WebhookReviewActivationService(
    IPullRequestIterationResolver iterationResolver,
    SubmitReviewJobHandler submitReviewJobHandler,
    ILogger<WebhookReviewActivationService> logger) : IWebhookReviewActivationService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ActivateAsync(
        WebhookConfigurationDto configuration,
        IncomingAdoWebhookDelivery delivery,
        AdoWebhookEventClassification classification,
        CancellationToken ct = default)
    {
        var iterationId = await iterationResolver.GetLatestIterationIdAsync(
            configuration.ClientId,
            configuration.OrganizationUrl,
            configuration.ProjectId,
            delivery.RepositoryId,
            delivery.PullRequestId,
            ct);

        var result = await submitReviewJobHandler.HandleAsync(
            new SubmitReviewJobCommand(
                configuration.ClientId,
                new SubmitReviewJobRequestDto(
                    configuration.OrganizationUrl,
                    configuration.ProjectId,
                    delivery.RepositoryId,
                    delivery.PullRequestId,
                    iterationId)),
            ct);

        if (result.IsDuplicate)
        {
            LogReviewIntakeDeduplicated(logger, delivery.PullRequestId, iterationId, classification.SummaryLabel);
            return
            [
                $"Skipped duplicate active job for PR #{delivery.PullRequestId} at iteration {iterationId} via {classification.SummaryLabel}.",
            ];
        }

        LogReviewIntakeSubmitted(logger, delivery.PullRequestId, iterationId, classification.SummaryLabel);
        return
        [
            $"Submitted review intake job for PR #{delivery.PullRequestId} at iteration {iterationId} via {classification.SummaryLabel}.",
        ];
    }

    [LoggerMessage(
        EventId = 2805,
        Level = LogLevel.Information,
        Message = "Submitted review intake for PR #{PullRequestId} iteration {IterationId} via {Trigger}.")]
    private static partial void LogReviewIntakeSubmitted(
        ILogger logger,
        int pullRequestId,
        int iterationId,
        string trigger);

    [LoggerMessage(
        EventId = 2806,
        Level = LogLevel.Information,
        Message =
            "Skipped duplicate webhook review intake for PR #{PullRequestId} iteration {IterationId} via {Trigger}.")]
    private static partial void LogReviewIntakeDeduplicated(
        ILogger logger,
        int pullRequestId,
        int iterationId,
        string trigger);
}
