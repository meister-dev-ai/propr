// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            IsActive = true,
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
        bool? enableEvidenceBackedVerification = null,
        bool? enableLanguageRobustScreening = null,
        bool? enableMultiPassUnion = null,
        bool? includeLinkedItemsInContext = null,
        IReadOnlyList<ReviewPassDto>? reviewPasses = null,
        ReviewReasoningEffort? baselineReasoningEffort = null,
        BudgetConfigDto? budgetConfig = null,
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

        ApplyScalarPatches(
            client,
            isActive,
            displayName,
            commentResolutionBehavior,
            customSystemMessage,
            defaultReviewPipelineProfileId,
            scmCommentPostingEnabled,
            enableEvidenceBackedVerification,
            enableLanguageRobustScreening,
            enableMultiPassUnion,
            includeLinkedItemsInContext,
            baselineReasoningEffort);
        ReplaceReviewPassesIfProvided(client, reviewPasses);
        ReplaceBudgetCapsIfProvided(client, budgetConfig);

        await dbContext.SaveChangesAsync(ct);
        return await this.GetByIdAsync(clientId, ct);
    }

    private static void ApplyScalarPatches(
        ClientRecord client,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior,
        string? customSystemMessage,
        string? defaultReviewPipelineProfileId,
        bool? scmCommentPostingEnabled,
        bool? enableEvidenceBackedVerification,
        bool? enableLanguageRobustScreening,
        bool? enableMultiPassUnion,
        bool? includeLinkedItemsInContext,
        ReviewReasoningEffort? baselineReasoningEffort)
    {
        ApplyIfHasValue(isActive, value => client.IsActive = value);
        ApplyIfNotNull(displayName, value => client.DisplayName = value);
        ApplyIfHasValue(commentResolutionBehavior, value => client.CommentResolutionBehavior = value);

        if (customSystemMessage is not null)
        {
            // Empty string clears the stored value; any other non-null value sets it.
            client.CustomSystemMessage = NormalizeToNullIfEmpty(customSystemMessage);
        }

        if (defaultReviewPipelineProfileId is not null)
        {
            ApplyDefaultReviewPipelineProfile(client, defaultReviewPipelineProfileId);
        }

        ApplyIfHasValue(scmCommentPostingEnabled, value => client.ScmCommentPostingEnabled = value);
        ApplyIfHasValue(enableEvidenceBackedVerification, value => client.EnableEvidenceBackedVerification = value);
        ApplyIfHasValue(enableLanguageRobustScreening, value => client.EnableLanguageRobustScreening = value);
        ApplyIfHasValue(enableMultiPassUnion, value => client.EnableMultiPassUnion = value);
        ApplyIfHasValue(includeLinkedItemsInContext, value => client.IncludeLinkedItemsInContext = value);
        ApplyIfHasValue(baselineReasoningEffort, value => client.BaselineReasoningEffort = value);
    }

    private static void ReplaceReviewPassesIfProvided(ClientRecord client, IReadOnlyList<ReviewPassDto>? reviewPasses)
    {
        if (reviewPasses is null)
        {
            return;
        }

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
                    Scope = pass.Scope,
                    Shadow = pass.Shadow,
                    // Store None as null so an unset effort keeps the column empty (null reads back as None).
                    ReasoningEffort = pass.ReasoningEffort == ReviewReasoningEffort.None ? null : pass.ReasoningEffort,
                });
        }
    }

    private static void ReplaceBudgetCapsIfProvided(ClientRecord client, BudgetConfigDto? budgetConfig)
    {
        if (budgetConfig is null)
        {
            return;
        }

        // Replace the budget caps as a group so an explicit null clears an individual cap; the per-field
        // "omit means unchanged" convention used above cannot express clearing a single nullable cap.
        client.MonthlyBudgetSoftCapUsd = budgetConfig.MonthlySoftCapUsd;
        client.MonthlyBudgetHardCapUsd = budgetConfig.MonthlyHardCapUsd;
        client.PullRequestBudgetSoftCapUsd = budgetConfig.PullRequestSoftCapUsd;
        client.PullRequestBudgetHardCapUsd = budgetConfig.PullRequestHardCapUsd;
        client.IncrementBudgetSoftCapUsd = budgetConfig.IncrementSoftCapUsd;
        client.IncrementBudgetHardCapUsd = budgetConfig.IncrementHardCapUsd;
    }

    private static void ApplyIfHasValue<T>(T? value, Action<T> apply) where T : struct
    {
        if (value.HasValue)
        {
            apply(value.Value);
        }
    }

    private static void ApplyIfNotNull<T>(T? value, Action<T> apply) where T : class
    {
        if (value is not null)
        {
            apply(value);
        }
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
        var profiles = (reviewPipelineProfileProvider?.GetProfiles() ?? [])
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
            .Select(pass => new ReviewPassDto(
                pass.Ordinal,
                pass.ConfiguredModelId,
                pass.Lens,
                pass.Scope,
                pass.Shadow,
                pass.ReasoningEffort ?? ReviewReasoningEffort.None))
            .ToList()
            .AsReadOnly();

        return new ClientDto(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage,
            client.DefaultReviewPipelineProfileId,
            client.DefaultReviewPipelineProfileUpdatedAtUtc,
            client.ScmCommentPostingEnabled,
            client.EnableEvidenceBackedVerification,
            client.EnableLanguageRobustScreening,
            client.EnableMultiPassUnion,
            client.IncludeLinkedItemsInContext,
            reviewPasses,
            client.BaselineReasoningEffort,
            tenantId,
            tenantSlug,
            tenantDisplayName,
            new BudgetConfigDto(
                client.MonthlyBudgetSoftCapUsd,
                client.MonthlyBudgetHardCapUsd,
                client.PullRequestBudgetSoftCapUsd,
                client.PullRequestBudgetHardCapUsd,
                client.IncrementBudgetSoftCapUsd,
                client.IncrementBudgetHardCapUsd));
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
