// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Runtime;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Parsing;

internal sealed class GitHubWebhookPayloadParser(GitHubWebhookEventClassifier eventClassifier)
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
        var eventName = ReadRequiredHeader(headers, "X-GitHub-Event");
        var deliveryId = ReadRequiredHeader(headers, "X-GitHub-Delivery");
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
            throw new InvalidOperationException("GitHub webhook payload is missing the repository object.");
        }

        var repositoryId = ReadRequiredString(repository, "id");
        var projectPath = ReadRequiredString(repository, "full_name");
        var owner = repository.TryGetProperty("owner", out var ownerElement)
            ? ReadRequiredString(ownerElement, "login")
            : projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException("GitHub webhook payload is missing the repository owner login.");
        }

        return new RepositoryRef(host, repositoryId, owner, projectPath);
    }

    private static CodeReviewRef ReadReview(RepositoryRef repository, JsonElement root)
    {
        if (!root.TryGetProperty("pull_request", out var pullRequest))
        {
            throw new InvalidOperationException("GitHub webhook payload is missing the pull_request object.");
        }

        return new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            ReadRequiredString(pullRequest, "id"),
            ReadRequiredInt32(pullRequest, "number"));
    }

    private static ReviewRevision ReadRevision(JsonElement root)
    {
        var headSha = ReadOptionalString(root, "pull_request", "head", "sha");
        var baseSha = ReadOptionalString(root, "pull_request", "base", "sha");
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            throw new InvalidOperationException("GitHub webhook payload is missing the required base or head SHA.");
        }

        return new ReviewRevision(headSha, baseSha, null, headSha, $"{baseSha}...{headSha}");
    }

    private static ReviewerIdentity? ReadActor(ProviderHostRef host, JsonElement root)
    {
        if (!root.TryGetProperty("sender", out var sender))
        {
            return null;
        }

        var externalUserId = ReadOptionalString(sender, "id");
        var login = ReadOptionalString(sender, "login");
        if (string.IsNullOrWhiteSpace(externalUserId) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        var type = ReadOptionalString(sender, "type");
        return new ReviewerIdentity(
            host,
            externalUserId,
            login,
            login,
            string.Equals(type, "Bot", StringComparison.OrdinalIgnoreCase) ||
            login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadRequiredHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(header.Value))
            {
                return header.Value.Trim();
            }
        }

        throw new InvalidOperationException($"GitHub webhook headers are missing the required {headerName} value.");
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"GitHub webhook payload is missing the required {propertyName} field.");
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(property.GetString()) => property.GetString()!.Trim(),
            JsonValueKind.Number => property.ToString(),
            _ => throw new InvalidOperationException($"GitHub webhook payload is missing the required {propertyName} field."),
        };
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidOperationException($"GitHub webhook payload is missing the required {propertyName} field.");
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
}
