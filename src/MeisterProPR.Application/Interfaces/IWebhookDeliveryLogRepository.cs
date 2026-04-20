// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for durable webhook delivery-history entries.</summary>
public interface IWebhookDeliveryLogRepository
{
    /// <summary>Persists a durable log entry for a webhook delivery.</summary>
    Task<WebhookDeliveryLogEntryDto> AddAsync(
        Guid webhookConfigurationId,
        DateTimeOffset receivedAt,
        string eventType,
        WebhookDeliveryOutcome deliveryOutcome,
        int httpStatusCode,
        string? repositoryId,
        int? pullRequestId,
        string? sourceBranch,
        string? targetBranch,
        IReadOnlyList<string> actionSummaries,
        string? failureReason,
        CancellationToken ct = default);

    /// <summary>Returns the most recent delivery-history entries for one webhook configuration.</summary>
    Task<IReadOnlyList<WebhookDeliveryLogEntryDto>> ListByWebhookConfigurationAsync(
        Guid webhookConfigurationId,
        int take = 50,
        CancellationToken ct = default);
}
