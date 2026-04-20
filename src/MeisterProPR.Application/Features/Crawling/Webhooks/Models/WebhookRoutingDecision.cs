// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Models;

/// <summary>Outcome of validating and routing one webhook delivery.</summary>
public sealed record WebhookRoutingDecision(
    WebhookDeliveryOutcome DeliveryOutcome,
    int HttpStatusCode,
    string? ResponseStatus,
    IReadOnlyList<string> ActionSummaries,
    string? FailureReason = null);
