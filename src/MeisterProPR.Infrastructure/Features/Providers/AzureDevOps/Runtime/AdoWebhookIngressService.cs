// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using static MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;

internal sealed class AdoWebhookIngressService(
    IAdoWebhookBasicAuthVerifier basicAuthVerifier,
    IAdoWebhookPayloadParser payloadParser,
    IClientRegistry clientRegistry) : IWebhookIngressService
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task<bool> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? verificationSecret = null,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(host);

        if (string.IsNullOrWhiteSpace(verificationSecret))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(
            basicAuthVerifier.IsAuthorized(
                TryReadHeader(headers, "Authorization"),
                verificationSecret));
    }

    public async Task<WebhookDeliveryEnvelope> ParseAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(host);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var delivery = payloadParser.Parse("providers/ado", root);
        var configuredReviewerId = await clientRegistry.GetReviewerIdAsync(clientId, ct);
        var repository = ReadRepository(host, root, delivery);

        return new WebhookDeliveryEnvelope(
            host,
            BuildDeliveryId(root, delivery),
            ClassifyDeliveryKind(delivery, configuredReviewerId),
            delivery.EventType,
            repository,
            new CodeReviewRef(
                repository,
                CodeReviewPlatformKind.PullRequest,
                delivery.PullRequestId.ToString(CultureInfo.InvariantCulture),
                delivery.PullRequestId),
            null,
            delivery.SourceBranch,
            delivery.TargetBranch,
            null);
    }

    private static string ClassifyDeliveryKind(IncomingAdoWebhookDelivery delivery, Guid? configuredReviewerId)
    {
        return delivery.NormalizedEventType switch
        {
            WebhookEventType.PullRequestCreated => "pull_request.created",
            WebhookEventType.PullRequestCommented => "pull_request.commented",
            WebhookEventType.PullRequestUpdated when !string.Equals(
                    delivery.PullRequestStatus,
                    "active",
                    StringComparison.OrdinalIgnoreCase)
                => string.Equals(delivery.PullRequestStatus, "completed", StringComparison.OrdinalIgnoreCase)
                    ? "pull_request.merged"
                    : "pull_request.closed",
            WebhookEventType.PullRequestUpdated when configuredReviewerId.HasValue &&
                                                     delivery.ReviewerIds.Contains(configuredReviewerId.Value)
                => "reviewer_assignment",
            WebhookEventType.PullRequestUpdated => "pull_request.updated",
            _ => throw new InvalidOperationException("Unsupported Azure DevOps webhook event type."),
        };
    }

    private static RepositoryRef ReadRepository(
        ProviderHostRef host,
        JsonElement payload,
        IncomingAdoWebhookDelivery delivery)
    {
        var repositoryName = TryReadString(payload, "resource", "repository", "name")
                             ?? delivery.RepositoryId;
        var projectIdOrName = TryReadString(payload, "resource", "repository", "project", "id")
                              ?? TryReadString(payload, "resource", "repository", "project", "name")
                              ?? repositoryName;

        return new RepositoryRef(host, delivery.RepositoryId, projectIdOrName, projectIdOrName);
    }

    private static string BuildDeliveryId(JsonElement payload, IncomingAdoWebhookDelivery delivery)
    {
        return TryReadString(payload, "id")
               ?? TryReadString(payload, "message", "id")
               ??
               $"{delivery.EventType}:{delivery.RepositoryId}:{delivery.PullRequestId.ToString(CultureInfo.InvariantCulture)}";
    }
}
