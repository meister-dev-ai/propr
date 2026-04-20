// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Runtime;

internal sealed class GitLabWebhookEventClassifier
{
    public GitLabWebhookEventClassification Classify(
        string eventName,
        JsonElement payload,
        ReviewerIdentity? configuredReviewer = null)
    {
        if (!string.Equals(eventName, "Merge Request Hook", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported GitLab webhook event type.");
        }

        if (!string.Equals(
                ReadRequiredString(payload, "object_kind"),
                "merge_request",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported GitLab webhook payload type.");
        }

        var action = ReadRequiredString(payload, "object_attributes", "action");
        if (string.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new GitLabWebhookEventClassification(
                WebhookEventType.PullRequestCreated,
                "pull_request.created",
                "pull request created",
                false);
        }

        if (string.Equals(action, "merge", StringComparison.OrdinalIgnoreCase))
        {
            return new GitLabWebhookEventClassification(
                WebhookEventType.PullRequestUpdated,
                "pull_request.merged",
                "pull request merged",
                true);
        }

        if (string.Equals(action, "close", StringComparison.OrdinalIgnoreCase))
        {
            return new GitLabWebhookEventClassification(
                WebhookEventType.PullRequestUpdated,
                "pull_request.closed",
                "pull request closed",
                true);
        }

        if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase) &&
            IsReviewerAssignment(payload, configuredReviewer))
        {
            return new GitLabWebhookEventClassification(
                WebhookEventType.PullRequestUpdated,
                "reviewer_assignment",
                "reviewer assignment",
                false);
        }

        return new GitLabWebhookEventClassification(
            WebhookEventType.PullRequestUpdated,
            "pull_request.updated",
            "pull request updated",
            false);
    }

    private static bool IsReviewerAssignment(JsonElement payload, ReviewerIdentity? configuredReviewer)
    {
        if (configuredReviewer is null || !TryGetPropertyPath(payload, out var changesElement, "changes"))
        {
            return false;
        }

        if (!TryReadChangedReviewers(changesElement, out var previousReviewers, out var currentReviewers))
        {
            return false;
        }

        var currentReviewer =
            currentReviewers.FirstOrDefault(candidate => MatchesReviewer(candidate, configuredReviewer));
        if (currentReviewer is null)
        {
            return false;
        }

        return currentReviewer.ReRequested ||
               !previousReviewers.Any(candidate => MatchesReviewer(candidate, configuredReviewer));
    }

    private static bool TryReadChangedReviewers(
        JsonElement changesElement,
        out IReadOnlyList<ReviewerCandidate> previousReviewers,
        out IReadOnlyList<ReviewerCandidate> currentReviewers)
    {
        previousReviewers = [];
        currentReviewers = [];

        if (!changesElement.TryGetProperty("reviewers", out var reviewersElement))
        {
            return false;
        }

        if (reviewersElement.ValueKind == JsonValueKind.Array)
        {
            var arrays = reviewersElement.EnumerateArray().ToArray();
            if (arrays.Length >= 2 && arrays[0].ValueKind == JsonValueKind.Array &&
                arrays[1].ValueKind == JsonValueKind.Array)
            {
                previousReviewers = ReadReviewers(arrays[0]);
                currentReviewers = ReadReviewers(arrays[1]);
                return true;
            }

            currentReviewers = ReadReviewers(reviewersElement);
            return currentReviewers.Count > 0;
        }

        if (reviewersElement.ValueKind == JsonValueKind.Object)
        {
            if (reviewersElement.TryGetProperty("previous", out var previousElement))
            {
                previousReviewers = ReadReviewers(previousElement);
            }

            if (reviewersElement.TryGetProperty("current", out var currentElement))
            {
                currentReviewers = ReadReviewers(currentElement);
            }

            return previousReviewers.Count > 0 || currentReviewers.Count > 0;
        }

        return false;
    }

    private static IReadOnlyList<ReviewerCandidate> ReadReviewers(JsonElement reviewersElement)
    {
        if (reviewersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var reviewers = new List<ReviewerCandidate>();
        foreach (var reviewer in reviewersElement.EnumerateArray())
        {
            if (reviewer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            reviewers.Add(
                new ReviewerCandidate(
                    ReadOptionalString(reviewer, "id"),
                    ReadOptionalString(reviewer, "username"),
                    ReadOptionalBoolean(reviewer, "re_requested") ?? false));
        }

        return reviewers.AsReadOnly();
    }

    private static bool MatchesReviewer(ReviewerCandidate candidate, ReviewerIdentity configuredReviewer)
    {
        return (!string.IsNullOrWhiteSpace(candidate.Id) && string.Equals(
                   candidate.Id,
                   configuredReviewer.ExternalUserId,
                   StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(candidate.Username) && string.Equals(
                   candidate.Username,
                   configuredReviewer.Login,
                   StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadRequiredString(JsonElement payload, params string[] path)
    {
        if (!TryGetPropertyPath(payload, out var property, path))
        {
            throw new InvalidOperationException($"GitLab webhook payload is missing the required {string.Join('.', path)} field.");
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(property.GetString()) => property.GetString()!.Trim(),
            JsonValueKind.Number => property.ToString(),
            _ => throw new InvalidOperationException($"GitLab webhook payload is missing the required {string.Join('.', path)} field."),
        };
    }

    private static string? ReadOptionalString(JsonElement payload, params string[] path)
    {
        if (!TryGetPropertyPath(payload, out var property, path))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => string.IsNullOrWhiteSpace(property.GetString())
                ? null
                : property.GetString()!.Trim(),
            JsonValueKind.Number => property.ToString(),
            _ => null,
        };
    }

    private static bool? ReadOptionalBoolean(JsonElement payload, params string[] path)
    {
        if (!TryGetPropertyPath(payload, out var property, path))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.True
            ? true
            : property.ValueKind == JsonValueKind.False
                ? false
                : null;
    }

    private static bool TryGetPropertyPath(JsonElement payload, out JsonElement property, params string[] path)
    {
        property = payload;
        foreach (var segment in path)
        {
            if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty(segment, out property))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record ReviewerCandidate(string? Id, string? Username, bool ReRequested);
}

internal sealed record GitLabWebhookEventClassification(
    WebhookEventType NormalizedEventType,
    string DeliveryKind,
    string SummaryLabel,
    bool RequiresLifecycleSync);
