// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;

/// <summary>Command envelope for one inbound provider-scoped webhook delivery.</summary>
public sealed record HandleProviderWebhookDeliveryCommand(
    ScmProvider Provider,
    string PathKey,
    IReadOnlyDictionary<string, string> Headers,
    string Payload,
    WebhookDeliveryProcessingMode Mode = WebhookDeliveryProcessingMode.AcceptAndQueue);

/// <summary>How far a delivery is taken on the caller's thread.</summary>
public enum WebhookDeliveryProcessingMode
{
    /// <summary>
    ///     Validate, then queue and answer. Everything up to this point reads only the delivery itself;
    ///     everything after it reads the provider, and that is what a provider's delivery timeout cannot
    ///     wait for.
    /// </summary>
    AcceptAndQueue = 0,

    /// <summary>Take it all the way to a review. What the queue worker does, off the request.</summary>
    Process = 1,
}
