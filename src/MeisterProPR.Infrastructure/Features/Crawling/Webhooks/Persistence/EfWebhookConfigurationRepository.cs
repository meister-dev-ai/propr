// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;

/// <summary>Database-backed webhook configuration repository.</summary>
public sealed class EfWebhookConfigurationRepository(
    MeisterProPRDbContext dbContext,
    IProviderActivationService? providerActivationService = null) : IWebhookConfigurationRepository
{
    /// <inheritdoc />
    public async Task<WebhookConfigurationDto> AddAsync(
        Guid clientId,
        WebhookProviderType providerType,
        string publicPathKey,
        string organizationUrl,
        string projectId,
        string secretCiphertext,
        IReadOnlyList<WebhookEventType> enabledEvents,
        Guid? organizationScopeId = null,
        CancellationToken ct = default,
        float? reviewTemperature = null)
    {
        var record = new WebhookConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ProviderType = providerType,
            PublicPathKey = publicPathKey,
            OrganizationScopeId = organizationScopeId,
            OrganizationUrl = organizationUrl,
            ProjectId = projectId,
            SecretCiphertext = secretCiphertext,
            IsActive = true,
            EnabledEvents = SerializeEnabledEvents(enabledEvents),
            ReviewTemperature = reviewTemperature,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.WebhookConfigurations.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default)
    {
        return this.DeleteInternalAsync(configId, clientId, ct);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        CancellationToken ct = default)
    {
        return dbContext.WebhookConfigurations.AnyAsync(
            config => config.ClientId == clientId &&
                      config.OrganizationUrl == organizationUrl &&
                      config.ProjectId == projectId,
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .Where(config => config.IsActive)
            .OrderByDescending(config => config.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(MapProviderType(record.ProviderType)))
                .ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookConfigurationDto>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .OrderByDescending(config => config.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(MapProviderType(record.ProviderType)))
                .ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookConfigurationDto>> GetByClientAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .Where(config => config.ClientId == clientId)
            .OrderByDescending(config => config.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(MapProviderType(record.ProviderType)))
                .ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookConfigurationDto>> GetByClientIdsAsync(
        IEnumerable<Guid> clientIds,
        CancellationToken ct = default)
    {
        var ids = clientIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var records = await this.BaseQuery()
            .Where(config => ids.Contains(config.ClientId))
            .OrderByDescending(config => config.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(MapProviderType(record.ProviderType)))
                .ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<WebhookConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default)
    {
        var record = await this.BaseQuery()
            .FirstOrDefaultAsync(config => config.Id == configId, ct);

        if (record is not null && providerActivationService is not null)
        {
            var enabled = await providerActivationService.IsEnabledAsync(MapProviderType(record.ProviderType), ct);
            if (!enabled)
            {
                return null;
            }
        }

        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<WebhookConfigurationDto?> GetActiveByPathKeyAsync(
        string publicPathKey,
        CancellationToken ct = default)
    {
        var record = await this.BaseQuery()
            .FirstOrDefaultAsync(config => config.PublicPathKey == publicPathKey && config.IsActive, ct);

        if (record is not null && providerActivationService is not null)
        {
            var enabled = await providerActivationService.IsEnabledAsync(MapProviderType(record.ProviderType), ct);
            if (!enabled)
            {
                return null;
            }
        }

        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid configId,
        bool? isActive,
        IReadOnlyList<WebhookEventType>? enabledEvents,
        Guid? ownerClientId,
        CancellationToken ct = default,
        float? reviewTemperature = null,
        bool shouldUpdateReviewTemperature = false)
    {
        var query = dbContext.WebhookConfigurations.Where(config => config.Id == configId);
        if (ownerClientId.HasValue)
        {
            query = query.Where(config => config.ClientId == ownerClientId.Value);
        }

        var record = await query.FirstOrDefaultAsync(ct);
        if (record is null)
        {
            return false;
        }

        if (isActive.HasValue)
        {
            record.IsActive = isActive.Value;
        }

        if (enabledEvents is not null)
        {
            record.EnabledEvents = SerializeEnabledEvents(enabledEvents);
        }

        if (shouldUpdateReviewTemperature)
        {
            record.ReviewTemperature = reviewTemperature;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRepoFiltersAsync(
        Guid configId,
        IReadOnlyList<WebhookRepoFilterDto> filters,
        CancellationToken ct = default)
    {
        var config = await dbContext.WebhookConfigurations
            .Include(c => c.RepoFilters)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);

        if (config is null)
        {
            return false;
        }

        dbContext.WebhookRepoFilters.RemoveRange(config.RepoFilters);

        foreach (var filter in filters)
        {
            config.RepoFilters.Add(
                new WebhookRepoFilterRecord
                {
                    Id = Guid.NewGuid(),
                    WebhookConfigurationId = configId,
                    RepositoryName = filter.RepositoryName,
                    SourceProvider = filter.CanonicalSourceRef?.Provider,
                    CanonicalSourceRef = filter.CanonicalSourceRef?.Value,
                    DisplayName = filter.DisplayName,
                    TargetBranchPatterns = filter.TargetBranchPatterns.ToArray(),
                });
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static WebhookConfigurationDto ToDto(WebhookConfigurationRecord record)
    {
        var enabledEvents = ParseEnabledEvents(record.EnabledEvents);

        return new WebhookConfigurationDto(
            record.Id,
            record.ClientId,
            record.ProviderType,
            record.PublicPathKey,
            record.OrganizationUrl,
            record.ProjectId,
            record.IsActive,
            record.CreatedAt,
            enabledEvents,
            record.RepoFilters
                .Select(static filter => new WebhookRepoFilterDto(
                    filter.Id,
                    filter.RepositoryName,
                    filter.TargetBranchPatterns,
                    filter.SourceProvider is not null && filter.CanonicalSourceRef is not null
                        ? new CanonicalSourceReferenceDto(filter.SourceProvider, filter.CanonicalSourceRef)
                        : null,
                    filter.DisplayName))
                .ToList()
                .AsReadOnly(),
            record.OrganizationScopeId,
            SecretCiphertext: record.SecretCiphertext,
            ReviewTemperature: record.ReviewTemperature);
    }

    private static string[] SerializeEnabledEvents(IReadOnlyList<WebhookEventType> enabledEvents)
    {
        return enabledEvents
            .Distinct()
            .Select(static value => value.ToString())
            .ToArray();
    }

    private static IReadOnlyList<WebhookEventType> ParseEnabledEvents(IEnumerable<string> enabledEventValues)
    {
        var parsedEvents = new List<WebhookEventType>();

        foreach (var enabledEventValue in enabledEventValues)
        {
            if (Enum.TryParse<WebhookEventType>(enabledEventValue, true, out var enabledEvent))
            {
                parsedEvents.Add(enabledEvent);
            }
        }

        return parsedEvents.AsReadOnly();
    }

    private IQueryable<WebhookConfigurationRecord> BaseQuery()
    {
        return dbContext.WebhookConfigurations
            .Include(config => config.RepoFilters);
    }

    private async Task<bool> DeleteInternalAsync(Guid configId, Guid clientId, CancellationToken ct)
    {
        var record = await dbContext.WebhookConfigurations
            .FirstOrDefaultAsync(config => config.Id == configId && config.ClientId == clientId, ct);

        if (record is null)
        {
            return false;
        }

        dbContext.WebhookConfigurations.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static ScmProvider MapProviderType(WebhookProviderType providerType)
    {
        return providerType switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => throw new ArgumentOutOfRangeException(nameof(providerType), providerType, null),
        };
    }
}
