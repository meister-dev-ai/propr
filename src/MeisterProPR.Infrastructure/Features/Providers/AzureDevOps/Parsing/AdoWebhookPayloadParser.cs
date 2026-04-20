// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Parsing;

/// <summary>Parses Azure DevOps webhook payloads into normalized delivery models.</summary>
public sealed class AdoWebhookPayloadParser : IAdoWebhookPayloadParser
{
    /// <inheritdoc />
    public IncomingAdoWebhookDelivery Parse(string pathKey, JsonElement payload)
    {
        var eventType = RequireString(payload, "eventType");
        var repositoryId = RequireString(payload, "resource", "repository", "id");
        var sourceBranch = RequireString(payload, "resource", "sourceRefName");
        var targetBranch = RequireString(payload, "resource", "targetRefName");
        var pullRequestStatus = RequireString(payload, "resource", "status");
        var reviewerIds = ReadReviewerIds(payload);

        if (!TryReadInt32(payload, out var pullRequestId, "resource", "pullRequestId"))
        {
            throw new InvalidOperationException("Payload is missing a valid pullRequestId.");
        }

        return new IncomingAdoWebhookDelivery(
            pathKey,
            eventType,
            NormalizeEventType(eventType),
            repositoryId,
            pullRequestId,
            sourceBranch,
            targetBranch,
            pullRequestStatus,
            reviewerIds);
    }

    private static WebhookEventType? NormalizeEventType(string eventType)
    {
        return eventType switch
        {
            "git.pullrequest.created" => WebhookEventType.PullRequestCreated,
            "git.pullrequest.updated" => WebhookEventType.PullRequestUpdated,
            "ms.vss-code.git-pullrequest-comment-event" => WebhookEventType.PullRequestCommented,
            "git.pullrequest.commented" => WebhookEventType.PullRequestCommented,
            _ => null,
        };
    }

    private static bool TryReadInt32(JsonElement element, out int value, params string[] path)
    {
        value = default;
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out value);
    }

    private static IReadOnlyList<Guid> ReadReviewerIds(JsonElement payload)
    {
        if (!payload.TryGetProperty("resource", out var resource) ||
            !resource.TryGetProperty("reviewers", out var reviewers) ||
            reviewers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var reviewerIds = new List<Guid>();
        foreach (var reviewer in reviewers.EnumerateArray())
        {
            if (!reviewer.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (Guid.TryParse(idElement.GetString(), out var reviewerId))
            {
                reviewerIds.Add(reviewerId);
            }
        }

        return reviewerIds;
    }

    private static string RequireString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                throw new InvalidOperationException($"Payload is missing required field '{string.Join('.', path)}'.");
            }
        }

        if (current.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(current.GetString()))
        {
            throw new InvalidOperationException($"Payload is missing required field '{string.Join('.', path)}'.");
        }

        return current.GetString()!;
    }
}
