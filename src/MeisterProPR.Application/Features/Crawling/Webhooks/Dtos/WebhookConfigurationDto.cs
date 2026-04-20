// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;

/// <summary>Data transfer object for a webhook configuration.</summary>
public sealed record WebhookConfigurationDto(
    Guid Id,
    Guid ClientId,
    WebhookProviderType ProviderType,
    string PublicPathKey,
    string OrganizationUrl,
    string ProjectId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WebhookEventType> EnabledEvents,
    IReadOnlyList<WebhookRepoFilterDto> RepoFilters,
    Guid? OrganizationScopeId = null,
    string? GeneratedSecret = null,
    string? SecretCiphertext = null);

/// <summary>Data transfer object for a webhook repository filter entry.</summary>
public sealed record WebhookRepoFilterDto(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Durable delivery-history entry for one webhook delivery.</summary>
public sealed record WebhookDeliveryLogEntryDto(
    Guid Id,
    Guid WebhookConfigurationId,
    DateTimeOffset ReceivedAt,
    string EventType,
    WebhookDeliveryOutcome DeliveryOutcome,
    int HttpStatusCode,
    string? RepositoryId,
    int? PullRequestId,
    string? SourceBranch,
    string? TargetBranch,
    IReadOnlyList<string> ActionSummaries,
    string? FailureReason,
    string? FailureCategory = null);
