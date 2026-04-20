// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client webhook configurations.</summary>
public interface IWebhookConfigurationRepository
{
    /// <summary>Adds a new webhook configuration for the given client.</summary>
    Task<WebhookConfigurationDto> AddAsync(
        Guid clientId,
        WebhookProviderType providerType,
        string publicPathKey,
        string organizationUrl,
        string projectId,
        string secretCiphertext,
        IReadOnlyList<WebhookEventType> enabledEvents,
        Guid? organizationScopeId = null,
        CancellationToken ct = default);

    /// <summary>Deletes a webhook configuration. Returns false if it does not exist or is not owned by the client.</summary>
    Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default);

    /// <summary>Returns true when a webhook configuration already exists for the same client, organization URL, and project.</summary>
    Task<bool> ExistsAsync(Guid clientId, string organizationUrl, string projectId, CancellationToken ct = default);

    /// <summary>Returns all active webhook configurations across all clients.</summary>
    Task<IReadOnlyList<WebhookConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>Returns all webhook configurations across all clients, including inactive ones.</summary>
    Task<IReadOnlyList<WebhookConfigurationDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns all webhook configurations owned by the given client.</summary>
    Task<IReadOnlyList<WebhookConfigurationDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns webhook configurations for the specified client IDs.</summary>
    Task<IReadOnlyList<WebhookConfigurationDto>> GetByClientIdsAsync(
        IEnumerable<Guid> clientIds,
        CancellationToken ct = default);

    /// <summary>Returns a webhook configuration by ID, or <see langword="null" /> if not found.</summary>
    Task<WebhookConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the active webhook configuration resolved by its public path key, or <see langword="null" /> if not
    ///     found.
    /// </summary>
    Task<WebhookConfigurationDto?> GetActiveByPathKeyAsync(string publicPathKey, CancellationToken ct = default);

    /// <summary>Applies partial updates to a webhook configuration.</summary>
    Task<bool> UpdateAsync(
        Guid configId,
        bool? isActive,
        IReadOnlyList<WebhookEventType>? enabledEvents,
        Guid? ownerClientId,
        CancellationToken ct = default);

    /// <summary>Replaces all repository filters for the given webhook configuration.</summary>
    Task<bool> UpdateRepoFiltersAsync(
        Guid configId,
        IReadOnlyList<WebhookRepoFilterDto> filters,
        CancellationToken ct = default);
}
