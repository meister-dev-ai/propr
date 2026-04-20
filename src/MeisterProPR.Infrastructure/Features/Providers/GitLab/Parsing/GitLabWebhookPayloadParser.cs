// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Runtime;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Parsing;

internal sealed class GitLabWebhookPayloadParser(GitLabWebhookEventClassifier eventClassifier)
{
    public WebhookDeliveryEnvelope Parse(
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        ReviewerIdentity? configuredReviewer = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventName = ReadRequiredHeader(headers, "X-Gitlab-Event");
        var deliveryId = ReadDeliveryId(headers, root);
        var classification = eventClassifier.Classify(eventName, root, configuredReviewer);
        var repository = ReadRepository(host, root);
        var review = ReadReview(repository, root);
        var revision = ReadRevision(root);

        return new WebhookDeliveryEnvelope(
            host,
            deliveryId,
            classification.DeliveryKind,
            eventName,
            repository,
            review,
            revision,
            ReadOptionalString(root, "object_attributes", "source_branch"),
            ReadOptionalString(root, "object_attributes", "target_branch"),
            ReadActor(host, root));
    }

    private static string ReadDeliveryId(IReadOnlyDictionary<string, string> headers, JsonElement root)
    {
        var headerValue = TryReadHeader(headers, "X-Gitlab-Event-UUID")
                          ?? TryReadHeader(headers, "X-Gitlab-Webhook-UUID")
                          ?? TryReadHeader(headers, "X-Request-Id");
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.Trim();
        }

        var reviewId = ReadOptionalString(root, "object_attributes", "id");
        var timestamp = ReadOptionalString(root, "object_attributes", "updated_at")
                        ?? ReadOptionalString(root, "object_attributes", "created_at");
        if (!string.IsNullOrWhiteSpace(reviewId) && !string.IsNullOrWhiteSpace(timestamp))
        {
            return $"{reviewId}:{timestamp}";
        }

        throw new InvalidOperationException("GitLab webhook headers are missing a delivery identifier.");
    }

    private static RepositoryRef ReadRepository(ProviderHostRef host, JsonElement root)
    {
        if (!root.TryGetProperty("project", out var project))
        {
            throw new InvalidOperationException("GitLab webhook payload is missing the project object.");
        }

        var repositoryId = ReadRequiredString(project, "id");
        var projectPath = ReadRequiredString(project, "path_with_namespace");
        var separatorIndex = projectPath.LastIndexOf('/');
        var ownerOrNamespace = separatorIndex > 0
            ? projectPath[..separatorIndex]
            : projectPath;

        return new RepositoryRef(host, repositoryId, ownerOrNamespace, projectPath);
    }

    private static CodeReviewRef ReadReview(RepositoryRef repository, JsonElement root)
    {
        if (!root.TryGetProperty("object_attributes", out var attributes))
        {
            throw new InvalidOperationException("GitLab webhook payload is missing the object_attributes object.");
        }

        return new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            ReadRequiredString(attributes, "id"),
            ReadRequiredInt32(attributes, "iid"));
    }

    private static ReviewRevision ReadRevision(JsonElement root)
    {
        var headSha = ReadOptionalString(root, "object_attributes", "last_commit", "id")
                      ?? ReadOptionalString(root, "object_attributes", "last_commit", "sha");
        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new InvalidOperationException("GitLab webhook payload is missing the last commit SHA.");
        }

        var oldRev = NormalizeSha(ReadOptionalString(root, "object_attributes", "oldrev"));
        var baseSha = oldRev ?? headSha;
        var startSha = oldRev ?? baseSha;
        return new ReviewRevision(headSha, baseSha, startSha, headSha, $"{baseSha}...{headSha}");
    }

    private static ReviewerIdentity? ReadActor(ProviderHostRef host, JsonElement root)
    {
        if (root.TryGetProperty("user", out var user))
        {
            var userId = ReadOptionalString(user, "id") ?? ReadOptionalString(root, "user_id");
            var username = ReadOptionalString(user, "username") ?? ReadOptionalString(root, "user_username");
            var displayName = ReadOptionalString(user, "name") ?? ReadOptionalString(root, "user_name") ?? username;
            if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(username))
            {
                return new ReviewerIdentity(
                    host,
                    userId,
                    username,
                    displayName ?? username,
                    username.EndsWith("bot", StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    private static string ReadRequiredHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        var headerValue = TryReadHeader(headers, headerName);
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.Trim();
        }

        throw new InvalidOperationException($"GitLab webhook headers are missing the required {headerName} value.");
    }

    private static string? TryReadHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"GitLab webhook payload is missing the required {propertyName} field.");
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(property.GetString()) => property.GetString()!.Trim(),
            JsonValueKind.Number => property.ToString(),
            _ => throw new InvalidOperationException($"GitLab webhook payload is missing the required {propertyName} field."),
        };
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidOperationException($"GitLab webhook payload is missing the required {propertyName} field.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => string.IsNullOrWhiteSpace(current.GetString()) ? null : current.GetString()!.Trim(),
            JsonValueKind.Number => current.ToString(),
            _ => null,
        };
    }

    private static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.All(character => character == '0')
            ? null
            : normalized;
    }
}
