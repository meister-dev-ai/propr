// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Implementation of <see cref="IClientAdminService" />.</summary>
public sealed class ClientAdminService(
    MeisterProPRDbContext dbContext,
    IReviewPipelineProfileProvider? reviewPipelineProfileProvider = null,
    IProviderActivationService? providerActivationService = null,
    ILicensingCapabilityService? licensingCapabilityService = null) : IClientAdminService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientDto>> GetAllAsync(CancellationToken ct = default)
    {
        var clients = await this.ClientsWithTenantQuery(await this.IsCommunityEditionAsync(ct))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return clients.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<ClientDto?> GetByIdAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await this.ClientsWithTenantQuery(await this.IsCommunityEditionAsync(ct))
            .SingleOrDefaultAsync(record => record.Id == clientId, ct);
        return client is null ? null : ToDto(client);
    }

    /// <inheritdoc />
    public async Task<ClientDto> CreateAsync(Guid tenantId, string displayName, CancellationToken ct = default)
    {
        return await this.CreateAsync(tenantId, displayName, ReviewStrategy.FileByFile, ct);
    }

    /// <inheritdoc />
    public async Task<ClientDto> CreateAsync(
        Guid tenantId,
        string displayName,
        ReviewStrategy defaultReviewStrategy,
        CancellationToken ct = default)
    {
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            IsActive = true,
            DefaultReviewStrategy = defaultReviewStrategy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync(ct);

        return await this.GetByIdAsync(client.Id, ct)
               ?? throw new InvalidOperationException($"Client {client.Id} was created but could not be reloaded.");
    }

    /// <inheritdoc />
    public async Task<ClientDto?> PatchAsync(
        Guid clientId,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior = null,
        string? customSystemMessage = null,
        string? defaultReviewPipelineProfileId = null,
        bool? scmCommentPostingEnabled = null,
        bool? enableProRV = null,
        bool? enableEvidenceBackedVerification = null,
        bool? enableMultiPassUnion = null,
        IReadOnlyList<ReviewPassDto>? reviewPasses = null,
        ReviewStrategy? defaultReviewStrategy = null,
        CancellationToken ct = default)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);
        var client = await dbContext.Clients
            .Include(record => record.ReviewPasses)
            .FirstOrDefaultAsync(record => record.Id == clientId, ct);
        if (client is null)
        {
            return null;
        }

        if (!TenantCatalog.IsClientVisible(client.TenantId, isCommunityEdition))
        {
            return null;
        }

        if (isActive.HasValue)
        {
            client.IsActive = isActive.Value;
        }

        if (displayName is not null)
        {
            client.DisplayName = displayName;
        }

        if (commentResolutionBehavior.HasValue)
        {
            client.CommentResolutionBehavior = commentResolutionBehavior.Value;
        }

        if (defaultReviewStrategy.HasValue)
        {
            client.DefaultReviewStrategy = defaultReviewStrategy.Value;
        }

        if (customSystemMessage is not null)
        {
            // Empty string clears the stored value; any other non-null value sets it.
            client.CustomSystemMessage = NormalizeToNullIfEmpty(customSystemMessage);
        }

        if (defaultReviewPipelineProfileId is not null)
        {
            ApplyDefaultReviewPipelineProfile(client, defaultReviewPipelineProfileId);
        }

        if (scmCommentPostingEnabled.HasValue)
        {
            client.ScmCommentPostingEnabled = scmCommentPostingEnabled.Value;
        }

        if (enableProRV.HasValue)
        {
            client.EnableProRV = enableProRV.Value;
        }

        if (enableEvidenceBackedVerification.HasValue)
        {
            client.EnableEvidenceBackedVerification = enableEvidenceBackedVerification.Value;
        }

        if (enableMultiPassUnion.HasValue)
        {
            client.EnableMultiPassUnion = enableMultiPassUnion.Value;
        }

        if (reviewPasses is not null)
        {
            // Wholesale replace the ordered pass list: clear the existing rows (cascade-deleted) and re-add the
            // requested entries with normalized sequential ordinals in the caller's declared order.
            client.ReviewPasses.Clear();
            var ordinal = 0;
            foreach (var pass in reviewPasses.OrderBy(entry => entry.Ordinal))
            {
                client.ReviewPasses.Add(
                    new ClientReviewPassRecord
                    {
                        Id = Guid.NewGuid(),
                        ClientId = client.Id,
                        Ordinal = ordinal++,
                        ConfiguredModelId = pass.ConfiguredModelId,
                        Lens = pass.Lens,
                    });
            }
        }

        await dbContext.SaveChangesAsync(ct);
        return await this.GetByIdAsync(clientId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid clientId, CancellationToken ct = default)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);
        var client = await dbContext.Clients.FindAsync([clientId], ct);
        if (client is null)
        {
            return false;
        }

        if (!TenantCatalog.IsClientVisible(client.TenantId, isCommunityEdition))
        {
            return false;
        }

        dbContext.Clients.Remove(client);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid clientId, CancellationToken ct = default)
    {
        return this.ExistsVisibleAsync(clientId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientDto>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var clients = await this.ClientsWithTenantQuery(await this.IsCommunityEditionAsync(ct))
            .Where(c => idList.Contains(c.Id))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return clients.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderConnectionAuditEntryDto>> GetProviderConnectionAuditTrailAsync(
        Guid clientId,
        int take = 20,
        CancellationToken ct = default)
    {
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "The take parameter must be greater than zero.");
        }

        await this.PurgeExpiredProviderAuditEntriesAsync(ct);

        var records = await dbContext.ProviderConnectionAuditEntries
            .AsNoTracking()
            .Where(entry => entry.ClientId == clientId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(take)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(record.Provider))
                .ToList();
        }

        return records
            .Select(record => new ProviderConnectionAuditEntryDto(
                record.Id,
                record.ClientId,
                record.ConnectionId,
                record.Provider,
                record.DisplayName,
                record.HostBaseUrl,
                record.EventType,
                record.Summary,
                record.OccurredAt,
                record.Status,
                record.FailureCategory,
                record.Detail))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewPipelineProfile>> GetSelectableReviewPipelineProfilesAsync(CancellationToken ct = default)
    {
        var profiles = (reviewPipelineProfileProvider?.GetProfiles(ReviewStrategy.FileByFile) ?? [])
            .Where(profile =>
                string.Equals(profile.ProfileId, ReviewPipelineProfileCatalog.FileByFileCalmProfileId, StringComparison.Ordinal)
                || string.Equals(profile.ProfileId, ReviewPipelineProfileCatalog.FileByFileBalancedProfileId, StringComparison.Ordinal)
                || string.Equals(profile.ProfileId, ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId, StringComparison.Ordinal))
            .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<ReviewPipelineProfile>>(profiles);
    }

    private static string? NormalizeToNullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void ApplyDefaultReviewPipelineProfile(ClientRecord client, string defaultReviewPipelineProfileId)
    {
        client.DefaultReviewPipelineProfileId = string.IsNullOrWhiteSpace(defaultReviewPipelineProfileId)
            ? null
            : defaultReviewPipelineProfileId;
        client.DefaultReviewPipelineProfileUpdatedAtUtc = client.DefaultReviewPipelineProfileId is null
            ? null
            : DateTimeOffset.UtcNow;
    }

    private IQueryable<ClientRecord> ClientsWithTenantQuery(bool isCommunityEdition)
    {
        IQueryable<ClientRecord> query = dbContext.Clients
            .AsNoTracking()
            .Include(client => client.Tenant)
            .Include(client => client.ReviewPasses);

        if (isCommunityEdition)
        {
            query = query.Where(client => client.TenantId == Guid.Empty || client.TenantId == TenantCatalog.SystemTenantId);
        }

        return query;
    }

    private async Task<bool> ExistsVisibleAsync(Guid clientId, CancellationToken ct)
    {
        return await this.ClientsWithTenantQuery(await this.IsCommunityEditionAsync(ct))
            .AnyAsync(c => c.Id == clientId, ct);
    }

    private async Task PurgeExpiredProviderAuditEntriesAsync(CancellationToken ct)
    {
        var cutoff = ProviderRetentionPolicy.GetProviderConnectionAuditCutoff(DateTimeOffset.UtcNow);
        var expiredEntries = await dbContext.ProviderConnectionAuditEntries
            .Where(entry => entry.OccurredAt < cutoff)
            .ToListAsync(ct);

        if (expiredEntries.Count == 0)
        {
            return;
        }

        dbContext.ProviderConnectionAuditEntries.RemoveRange(expiredEntries);
        await dbContext.SaveChangesAsync(ct);
    }

    private static ClientDto ToDto(ClientRecord client)
    {
        var tenantId = client.TenantId == Guid.Empty
            ? TenantCatalog.SystemTenantId
            : client.TenantId;
        var tenantSlug = client.Tenant?.Slug;
        var tenantDisplayName = client.Tenant?.DisplayName;

        if (TenantCatalog.IsSystemTenant(tenantId))
        {
            tenantSlug ??= TenantCatalog.SystemTenantSlug;
            tenantDisplayName ??= TenantCatalog.SystemTenantDisplayName;
        }

        var reviewPasses = client.ReviewPasses
            .OrderBy(pass => pass.Ordinal)
            .Select(pass => new ReviewPassDto(pass.Ordinal, pass.ConfiguredModelId, pass.Lens))
            .ToList()
            .AsReadOnly();

        return new ClientDto(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.CommentResolutionBehavior,
            client.DefaultReviewStrategy,
            client.CustomSystemMessage,
            client.DefaultReviewPipelineProfileId,
            client.DefaultReviewPipelineProfileUpdatedAtUtc,
            client.ScmCommentPostingEnabled,
            client.EnableProRV,
            client.EnableEvidenceBackedVerification,
            client.EnableMultiPassUnion,
            reviewPasses,
            tenantId,
            tenantSlug,
            tenantDisplayName);
    }

    private async Task<bool> IsCommunityEditionAsync(CancellationToken ct)
    {
        if (licensingCapabilityService is null)
        {
            return false;
        }

        var summaryTask = licensingCapabilityService.GetSummaryAsync(ct);
        if (summaryTask is null)
        {
            return false;
        }

        var summary = await summaryTask;
        return summary?.Edition == InstallationEdition.Community;
    }
}
