// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Runtime;

internal sealed class ForgejoWebhookEventClassifier
{
    public ForgejoWebhookEventClassification Classify(
        string eventName,
        JsonElement payload,
        ReviewerIdentity? configuredReviewer = null)
    {
        if (!string.Equals(eventName, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported Forgejo webhook event type.");
        }

        var action = ReadRequiredString(payload, "action");
        if (string.Equals(action, "opened", StringComparison.OrdinalIgnoreCase))
        {
            return new ForgejoWebhookEventClassification(
                WebhookEventType.PullRequestCreated,
                "pull_request.created",
                "pull request created",
                false);
        }

        if (string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var merged = ReadOptionalBoolean(payload, "pull_request", "merged") == true;
            return new ForgejoWebhookEventClassification(
                WebhookEventType.PullRequestUpdated,
                merged ? "pull_request.merged" : "pull_request.closed",
                merged ? "pull request merged" : "pull request closed",
                true);
        }

        if (string.Equals(action, "review_requested", StringComparison.OrdinalIgnoreCase) &&
            IsRequestedReviewerMatch(payload, configuredReviewer))
        {
            return new ForgejoWebhookEventClassification(
                WebhookEventType.PullRequestUpdated,
                "reviewer_assignment",
                "reviewer assignment",
                false);
        }

        return new ForgejoWebhookEventClassification(
            WebhookEventType.PullRequestUpdated,
            "pull_request.updated",
            "pull request updated",
            false);
    }

    private static bool IsRequestedReviewerMatch(JsonElement payload, ReviewerIdentity? configuredReviewer)
    {
        if (configuredReviewer is null
            || !payload.TryGetProperty("requested_reviewer", out var requestedReviewer)
            || !requestedReviewer.TryGetProperty("login", out var loginProperty))
        {
            return false;
        }

        var requestedLogin = loginProperty.GetString();
        return !string.IsNullOrWhiteSpace(requestedLogin)
               && string.Equals(requestedLogin, configuredReviewer.Login, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"Forgejo webhook payload is missing the required {propertyName} field.");
        }

        return property.GetString()!.Trim();
    }

    private static bool? ReadOptionalBoolean(JsonElement payload, params string[] path)
    {
        var current = payload;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.True
            ? true
            : current.ValueKind == JsonValueKind.False
                ? false
                : null;
    }
}

internal sealed record ForgejoWebhookEventClassification(
    WebhookEventType NormalizedEventType,
    string DeliveryKind,
    string SummaryLabel,
    bool RequiresLifecycleSync);
