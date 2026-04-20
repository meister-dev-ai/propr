// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Ports;

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
