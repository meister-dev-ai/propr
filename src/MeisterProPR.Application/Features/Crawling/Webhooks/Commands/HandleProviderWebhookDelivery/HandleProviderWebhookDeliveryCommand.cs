// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;

/// <summary>Command envelope for one inbound provider-scoped webhook delivery.</summary>
public sealed record HandleProviderWebhookDeliveryCommand(
    ScmProvider Provider,
    string PathKey,
    IReadOnlyDictionary<string, string> Headers,
    string Payload);
