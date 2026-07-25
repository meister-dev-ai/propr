// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Models;

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>Activates review intake for accepted webhook deliveries.</summary>
public interface IWebhookReviewActivationService
{
    /// <summary>Submits or deduplicates review intake for one classified webhook delivery.</summary>
    Task<IReadOnlyList<string>> ActivateAsync(
        WebhookConfigurationDto configuration,
        IncomingAdoWebhookDelivery delivery,
        AdoWebhookEventClassification classification,
        CancellationToken ct = default);
}
