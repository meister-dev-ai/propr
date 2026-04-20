// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Services;

/// <summary>Reuses review-job cancellation semantics for webhook-delivered PR closure events.</summary>
public sealed partial class WebhookReviewLifecycleSyncService(
    IJobRepository jobRepository,
    ILogger<WebhookReviewLifecycleSyncService> logger) : IWebhookReviewLifecycleSyncService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SynchronizeAsync(
        WebhookConfigurationDto configuration,
        IncomingAdoWebhookDelivery delivery,
        AdoWebhookEventClassification classification,
        CancellationToken ct = default)
    {
        var activeJobs = await jobRepository.GetActiveJobsForConfigAsync(
            configuration.OrganizationUrl,
            configuration.ProjectId,
            ct);
        var matchingJobs = activeJobs
            .Where(job => string.Equals(job.RepositoryId, delivery.RepositoryId, StringComparison.OrdinalIgnoreCase)
                          && job.PullRequestId == delivery.PullRequestId)
            .ToList();

        if (matchingJobs.Count == 0)
        {
            LogLifecycleNoOp(logger, delivery.PullRequestId, delivery.PullRequestStatus);
            return
            [
                $"No active review jobs required cancellation for PR #{delivery.PullRequestId} because the pull request is {delivery.PullRequestStatus}.",
            ];
        }

        foreach (var job in matchingJobs)
        {
            await jobRepository.SetCancelledAsync(job.Id, ct);
        }

        LogLifecycleCancelled(logger, delivery.PullRequestId, matchingJobs.Count, delivery.PullRequestStatus);
        return
        [
            $"Cancelled {matchingJobs.Count} active review job(s) for PR #{delivery.PullRequestId} because the pull request is {delivery.PullRequestStatus}.",
        ];
    }

    [LoggerMessage(
        EventId = 2807,
        Level = LogLevel.Information,
        Message =
            "Cancelled {CancelledCount} active webhook review job(s) for PR #{PullRequestId} because the status is {PullRequestStatus}.")]
    private static partial void LogLifecycleCancelled(
        ILogger logger,
        int pullRequestId,
        int cancelledCount,
        string pullRequestStatus);

    [LoggerMessage(
        EventId = 2808,
        Level = LogLevel.Information,
        Message =
            "No active webhook review jobs required cancellation for PR #{PullRequestId}; status is {PullRequestStatus}.")]
    private static partial void LogLifecycleNoOp(ILogger logger, int pullRequestId, string pullRequestStatus);
}
