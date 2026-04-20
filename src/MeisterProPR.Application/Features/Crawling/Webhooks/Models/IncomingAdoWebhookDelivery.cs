// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Models;

/// <summary>Normalized representation of one inbound Azure DevOps webhook delivery.</summary>
public sealed record IncomingAdoWebhookDelivery(
    string PathKey,
    string EventType,
    WebhookEventType? NormalizedEventType,
    string RepositoryId,
    int PullRequestId,
    string SourceBranch,
    string TargetBranch,
    string PullRequestStatus,
    IReadOnlyList<Guid> ReviewerIds);
