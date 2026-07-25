// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Models;

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>Synchronizes review-job lifecycle when a webhook delivery closes a pull request.</summary>
public interface IWebhookReviewLifecycleSyncService
{
    /// <summary>Cancels or suppresses active review work for one classified webhook delivery.</summary>
    Task<IReadOnlyList<string>> SynchronizeAsync(
        WebhookConfigurationDto configuration,
        IncomingAdoWebhookDelivery delivery,
        AdoWebhookEventClassification classification,
        CancellationToken ct = default);
}
