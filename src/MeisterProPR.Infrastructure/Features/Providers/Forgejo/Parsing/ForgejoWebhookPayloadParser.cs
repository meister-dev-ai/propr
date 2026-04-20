// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Runtime;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Parsing;

internal sealed class ForgejoWebhookPayloadParser(ForgejoWebhookEventClassifier eventClassifier)
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
        var eventName = ReadRequiredHeader(headers, "X-Gitea-Event", "X-GitHub-Event");
        var deliveryId = ReadRequiredHeader(headers, "X-Gitea-Delivery", "X-GitHub-Delivery");
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
            ReadOptionalString(root, "pull_request", "head", "ref"),
            ReadOptionalString(root, "pull_request", "base", "ref"),
            ReadActor(host, root));
    }

    private static RepositoryRef ReadRepository(ProviderHostRef host, JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repository))
        {
            throw new InvalidOperationException("Forgejo webhook payload is missing the repository object.");
        }

        var repositoryId = ReadRequiredString(repository, "id");
        var projectPath = ReadRequiredString(repository, "full_name");
        var owner = repository.TryGetProperty("owner", out var ownerElement)
            ? ReadOptionalString(ownerElement, "login") ?? ReadOptionalString(ownerElement, "username")
            : null;
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException("Forgejo webhook payload is missing the repository owner login.");
        }

        return new RepositoryRef(host, repositoryId, owner, projectPath);
    }

    private static CodeReviewRef ReadReview(RepositoryRef repository, JsonElement root)
    {
        if (!root.TryGetProperty("pull_request", out var pullRequest))
        {
            throw new InvalidOperationException("Forgejo webhook payload is missing the pull_request object.");
        }

        var number = ReadOptionalInt32(root, "number") ?? ReadRequiredInt32(pullRequest, "number");
        return new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            ReadRequiredString(pullRequest, "id"),
            number);
    }

    private static ReviewRevision ReadRevision(JsonElement root)
    {
        var headSha = ReadOptionalString(root, "pull_request", "head", "sha");
        var baseSha = ReadOptionalString(root, "pull_request", "base", "sha");
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            throw new InvalidOperationException("Forgejo webhook payload is missing the required base or head SHA.");
        }

        return new ReviewRevision(headSha, baseSha, baseSha, headSha, $"{baseSha}...{headSha}");
    }

    private static ReviewerIdentity? ReadActor(ProviderHostRef host, JsonElement root)
    {
        if (!root.TryGetProperty("sender", out var sender))
        {
            return null;
        }

        var externalUserId = ReadOptionalString(sender, "id");
        var login = ReadOptionalString(sender, "login") ?? ReadOptionalString(sender, "username");
        if (string.IsNullOrWhiteSpace(externalUserId) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        var displayName = ReadOptionalString(sender, "full_name") ?? login;
        return new ReviewerIdentity(
            host,
            externalUserId,
            login,
            displayName ?? login,
            login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
            || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
            || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadRequiredHeader(IReadOnlyDictionary<string, string> headers, params string[] headerNames)
    {
        foreach (var headerName in headerNames)
        {
            var value = TryReadHeader(headers, headerName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        throw new InvalidOperationException("Forgejo webhook headers are missing a required value.");
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
            throw new InvalidOperationException($"Forgejo webhook payload is missing the required {propertyName} field.");
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(property.GetString()) => property.GetString()!.Trim(),
            JsonValueKind.Number => property.ToString(),
            _ => throw new InvalidOperationException($"Forgejo webhook payload is missing the required {propertyName} field."),
        };
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidOperationException($"Forgejo webhook payload is missing the required {propertyName} field.");
        }

        return value;
    }

    private static int? ReadOptionalInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
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
}
