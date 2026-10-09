// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using System.Security.Cryptography;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>Database-backed crawl configuration repository.</summary>
public sealed class CrawlConfigurationRepository(
    MeisterProPRDbContext dbContext,
    IProviderActivationService? providerActivationService = null)
    : ICrawlConfigurationRepository
{
    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireReviewTargetAdmissionAsync(Guid clientId, CancellationToken ct = default)
    {
        var source = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        return PostgresAdvisoryLocks.AcquireSessionAsync(source, $"review-target:{clientId:D}", "review-target-admission", ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetManagementTargetsAsync(Guid clientId, CancellationToken ct = default)
    {
        var records = await this.BaseQuery().AsNoTracking().Where(c => c.ClientId == clientId).ToListAsync(ct).ConfigureAwait(false);
        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationPageDto> GetManagementTargetPageAsync(
        Guid clientId, string? search, ScmProvider? provider,
        ReviewTargetLifecycle? lifecycle, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        var query = this.ManagementTargetQuery(clientId, search, provider, lifecycle);
        await using var snapshot = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var snapshotVersion = await ReadManagementSnapshotVersionAsync(query, ct).ConfigureAwait(false);
        var records = await query.OrderBy(c => c.RepoFilters.Select(f => f.RepositoryName.ToLower()).First()).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        await snapshot.CommitAsync(ct).ConfigureAwait(false);
        return new(records.Select(ToDto).ToList(), total, page, pageSize, snapshotVersion);
    }

    private IQueryable<CrawlConfigurationRecord> ManagementTargetQuery(Guid clientId, string? search, ScmProvider? provider, ReviewTargetLifecycle? lifecycle)
    {
        var query = this.BaseQuery().AsNoTracking().Where(c => c.ClientId == clientId && c.ReviewTargetLifecycle != ReviewTargetLifecycle.Removed &&
                                                               c.RepoFilters.Count == 1 && c.RepoFilters.Any(f => f.CanonicalSourceRef != null));
        if (provider.HasValue)
        {
            query = query.Where(c => c.Provider == provider.Value);
        }

        if (lifecycle.HasValue)
        {
            query = query.Where(c => c.ReviewTargetLifecycle == lifecycle.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(c => c.OrganizationUrl.ToLower().Contains(normalized) || c.ProjectId.ToLower().Contains(normalized) ||
                                     c.RepoFilters.Any(f => f.RepositoryName.ToLower().Contains(normalized)));
        }

        return query;
    }

    private static async Task<string> ReadManagementSnapshotVersionAsync(IQueryable<CrawlConfigurationRecord> query, CancellationToken ct)
    {
        using var representation = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var metadata = query.OrderBy(c => c.Id).Select(c => new
        {
            c.Id, c.ReviewTargetRevision, c.Provider, c.OrganizationUrl, c.ProjectId, c.RepositoryId,
            c.ReviewTargetLifecycle, c.IsActive,
            Filters = c.RepoFilters.OrderBy(f => f.Id).Select(f => new
            {
                f.Id, f.CanonicalSourceRef, f.SourceProvider, f.RepositoryName, f.TargetBranchPatterns,
            }).ToArray(),
        });
        await foreach (var item in metadata.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            representation.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
        }

        return Convert.ToHexStringLower(representation.GetHashAndReset());
    }

    /// <inheritdoc />
    public async Task<bool> ChangeReviewTargetLifecycleAsync(
        Guid targetId, Guid clientId, long expectedRevision, ReviewTargetLifecycle lifecycle, CancellationToken ct = default)
    {
        if (expectedRevision < 1 || !Enum.IsDefined(lifecycle))
        {
            return false;
        }

        await using var admission = await this.AcquireReviewTargetAdmissionAsync(clientId, ct).ConfigureAwait(false);
        var count = await dbContext.CrawlConfigurations.Where(c => c.Id == targetId && c.ClientId == clientId &&
                                                                   c.ReviewTargetRevision == expectedRevision &&
                                                                   c.ReviewTargetLifecycle != ReviewTargetLifecycle.Removed &&
                                                                   c.RepoFilters.Count == 1 && c.RepoFilters.Any(f => f.CanonicalSourceRef != null))
            .ExecuteUpdateAsync(
                set => set.SetProperty(c => c.ReviewTargetLifecycle, lifecycle)
                    .SetProperty(c => c.IsActive, false).SetProperty(c => c.ReviewTargetRevision, c => c.ReviewTargetRevision + 1), ct).ConfigureAwait(false);
        return count == 1;
    }

    /// <inheritdoc />
    public async Task<bool> RestoreReviewTargetAsync(
        CrawlConfigurationDto expectedTarget, Guid clientId, IReadOnlyList<string> targetBranchPatterns, CancellationToken ct = default)
    {
        if (expectedTarget.RepoFilters is not { Count: 1 } || expectedTarget.RepoFilters[0].CanonicalSourceRef is null)
        {
            return false;
        }

        if (targetBranchPatterns is null || !DestinationBranchPolicy.TryCreate(targetBranchPatterns, out var policy))
        {
            return false;
        }

        await using var admission = await this.AcquireReviewTargetAdmissionAsync(clientId, ct).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var count = await this.RestorationCandidateQuery(expectedTarget, clientId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(c => c.ReviewTargetLifecycle, ReviewTargetLifecycle.Enabled)
                    .SetProperty(c => c.IsActive, false).SetProperty(c => c.ReviewTargetRevision, c => c.ReviewTargetRevision + 1), ct).ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        var replacement = JsonSerializer.Serialize(policy!.Patterns);
        var changed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE crawl_repo_filters SET target_branch_patterns = {replacement}::jsonb WHERE crawl_configuration_id = {expectedTarget.Id} AND id = {expectedTarget.RepoFilters[0].Id}",
            ct).ConfigureAwait(false);
        if (changed != 1)
        {
            return false;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private IQueryable<CrawlConfigurationRecord> RestorationCandidateQuery(CrawlConfigurationDto expectedTarget, Guid clientId)
    {
        var filter = expectedTarget.RepoFilters[0];
        var reference = filter.CanonicalSourceRef!;
        return dbContext.CrawlConfigurations
            .Where(c => c.Id == expectedTarget.Id && c.ClientId == clientId)
            .Where(c => c.ReviewTargetRevision == expectedTarget.ReviewTargetRevision && c.ReviewTargetLifecycle == ReviewTargetLifecycle.Removed)
            .Where(c => c.Provider == expectedTarget.Provider && c.OrganizationUrl == expectedTarget.ProviderScopePath &&
                        c.ProjectId == expectedTarget.ProviderProjectKey && c.OrganizationScopeId == expectedTarget.OrganizationScopeId)
            .Where(c => c.RepoFilters.Count == 1 && c.RepoFilters.Any(f => f.Id == filter.Id && f.RepositoryName == filter.RepositoryName &&
                                                                           f.SourceProvider == reference.Provider && f.CanonicalSourceRef == reference.Value));
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(Guid configId, Guid clientId, bool isActive, CancellationToken ct = default)
    {
        var owner = await dbContext.CrawlConfigurations.AsNoTracking().Where(c => c.Id == configId && c.ClientId == clientId)
            .Select(c => new { Canonical = c.RepoFilters.Any(f => f.CanonicalSourceRef != null) }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner is null)
        {
            return false;
        }

        await using var admission = owner.Canonical ? await this.AcquireReviewTargetAdmissionAsync(clientId, ct).ConfigureAwait(false) : null;
        var changed = await dbContext.CrawlConfigurations
            .Where(c => c.Id == configId && c.ClientId == clientId && (!isActive || c.ReviewTargetLifecycle == ReviewTargetLifecycle.Enabled) &&
                        (owner.Canonical || !c.RepoFilters.Any(f => f.CanonicalSourceRef != null)))
            .ExecuteUpdateAsync(
                set => set.SetProperty(c => c.IsActive, isActive)
                    .SetProperty(c => c.ReviewTargetRevision, c => c.ReviewTargetRevision + 1), ct).ConfigureAwait(false);
        if (changed != 1)
        {
            return false;
        }

        var tracked = dbContext.CrawlConfigurations.Local.SingleOrDefault(c => c.Id == configId);
        if (tracked is not null)
        {
            await dbContext.Entry(tracked).ReloadAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto> AddAsync(
        Guid clientId,
        ScmProvider provider,
        string organizationUrl,
        string projectId,
        int crawlIntervalSeconds,
        Guid? organizationScopeId = null,
        CancellationToken ct = default,
        float? reviewTemperature = null)
    {
        var record = new CrawlConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Provider = provider,
            OrganizationUrl = organizationUrl,
            ProjectId = projectId,
            OrganizationScopeId = organizationScopeId,
            CrawlIntervalSeconds = crawlIntervalSeconds,
            ReviewTemperature = reviewTemperature,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.CrawlConfigurations.Add(record);
        await dbContext.SaveChangesAsync(ct);

        return new CrawlConfigurationDto(
            record.Id,
            record.ClientId,
            record.Provider,
            record.OrganizationUrl,
            record.ProjectId,
            record.CrawlIntervalSeconds,
            record.IsActive,
            record.CreatedAt,
            [],
            record.OrganizationScopeId,
            ReviewTemperature: record.ReviewTemperature);
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto> AddReviewTargetAsync(
        Guid clientId,
        ScmProvider provider,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        string repositoryName,
        CancellationToken ct = default,
        IReadOnlyList<string>? targetBranchPatterns = null)
    {
        var policy = DestinationBranchPolicy.Create(targetBranchPatterns ?? []);
        var record = new CrawlConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Provider = provider,
            OrganizationUrl = providerScopePath,
            ProjectId = providerProjectKey,
            RepositoryId = repositoryId,
            CrawlIntervalSeconds = 60,
            IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow,
            RepoFilters =
            [
                new CrawlRepoFilterRecord
                {
                    Id = Guid.NewGuid(),
                    RepositoryName = repositoryName,
                    DisplayName = repositoryName,
                    SourceProvider = provider.ToString(),
                    CanonicalSourceRef = repositoryId,
                    TargetBranchPatterns = policy.Patterns.ToArray(),
                },
            ],
        };
        dbContext.CrawlConfigurations.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateReviewTargetPolicyAsync(
        CrawlConfigurationDto expectedTarget, Guid clientId, IReadOnlyList<string> expectedTargetBranchPatterns,
        IReadOnlyList<string> targetBranchPatterns, CancellationToken ct = default)
    {
        var policy = DestinationBranchPolicy.Create(targetBranchPatterns);
        DestinationBranchPolicy.Create(expectedTargetBranchPatterns);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        await using var admission = await this.AcquireReviewTargetAdmissionAsync(clientId, ct).ConfigureAwait(false);
        if (expectedTarget.ClientId != clientId)
        {
            return false;
        }

        if (expectedTarget.RepoFilters.Count != 1 || expectedTarget.RepoFilters[0].CanonicalSourceRef is not { } reference)
        {
            return false;
        }

        var filter = expectedTarget.RepoFilters[0];
        var expected = JsonSerializer.Serialize(expectedTargetBranchPatterns);
        var replacement = JsonSerializer.Serialize(policy.Patterns);
        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             WITH authorized_configuration AS MATERIALIZED (
                 SELECT id FROM crawl_configurations
                 WHERE id = {expectedTarget.Id} AND client_id = {clientId}
                   AND provider = {(int)expectedTarget.Provider}
                   AND organization_url = {expectedTarget.ProviderScopePath}
                   AND project_id = {expectedTarget.ProviderProjectKey}
                   AND organization_scope_id IS NOT DISTINCT FROM {expectedTarget.OrganizationScopeId}
                   AND review_target_lifecycle <> 2 AND review_target_revision = {expectedTarget.ReviewTargetRevision}
                 FOR UPDATE
             ), changed_filter AS (
             UPDATE crawl_repo_filters AS filter
             SET target_branch_patterns = {replacement}::jsonb
             FROM authorized_configuration AS config
             WHERE filter.crawl_configuration_id = config.id
               AND filter.id = {filter.Id}
               AND filter.repository_name = {filter.RepositoryName}
               AND filter.source_provider = {reference.Provider}
               AND filter.canonical_source_ref = {reference.Value}
               AND filter.canonical_source_ref IS NOT NULL AND filter.canonical_source_ref <> ''
               AND filter.target_branch_patterns = {expected}::jsonb
               AND (SELECT COUNT(*) FROM crawl_repo_filters AS sibling WHERE sibling.crawl_configuration_id = config.id) = 1
             RETURNING filter.crawl_configuration_id
             )
             UPDATE crawl_configurations SET review_target_revision = review_target_revision + 1
             WHERE id IN (SELECT crawl_configuration_id FROM changed_filter)
             """, ct).ConfigureAwait(false);
        return updated == 1;
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto?> GetReviewTargetPolicySnapshotAsync(Guid configId, Guid clientId, CancellationToken ct = default)
    {
        var record = await this.BaseQuery().AsNoTracking().SingleOrDefaultAsync(config => config.Id == configId && config.ClientId == clientId, ct);
        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .Where(c => c.IsActive && c.ReviewTargetLifecycle == ReviewTargetLifecycle.Enabled)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records.Where(record => enabledProviders.Contains(record.Provider)).ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records.Where(record => enabledProviders.Contains(record.Provider)).ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string? repositoryId,
        string? branchFilter,
        CancellationToken ct = default)
    {
        return dbContext.CrawlConfigurations.AnyAsync(
            c => c.ClientId == clientId &&
                 c.OrganizationUrl == organizationUrl &&
                 c.ProjectId == projectId &&
                 (repositoryId == null ? c.RepositoryId == null : c.RepositoryId == repositoryId) &&
                 (branchFilter == null ? c.BranchFilter == null : c.BranchFilter == branchFilter),
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default)
    {
        var changed = await dbContext.CrawlConfigurations.Where(c => c.Id == configId && c.ClientId == clientId &&
                                                                     !(c.RepoFilters.Count == 1 && c.RepoFilters.Any(f => f.CanonicalSourceRef != null)) &&
                                                                     (c.ReviewTargetLifecycle != ReviewTargetLifecycle.Removed ||
                                                                      !c.RepoFilters.Any(f => f.CanonicalSourceRef != null)))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (changed != 1)
        {
            return false;
        }

        var tracked = dbContext.CrawlConfigurations.Local.SingleOrDefault(c => c.Id == configId);
        if (tracked is not null)
        {
            dbContext.Entry(tracked).State = EntityState.Detached;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await this.BaseQuery().AsNoTracking()
            .Where(c => c.ClientId == clientId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records.Where(record => enabledProviders.Contains(record.Provider)).ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientIdsAsync(
        IEnumerable<Guid> clientIds,
        CancellationToken ct = default)
    {
        var ids = clientIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var records = await this.BaseQuery().AsNoTracking()
            .Where(c => ids.Contains(c.ClientId))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records.Where(record => enabledProviders.Contains(record.Provider)).ToList();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default)
    {
        var record = await this.BaseQuery()
            .Where(c => c.Id == configId)
            .FirstOrDefaultAsync(ct);

        if (record is not null && providerActivationService is not null)
        {
            var enabled = await providerActivationService.IsEnabledAsync(record.Provider, ct);
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
        int? crawlIntervalSeconds,
        bool? isActive,
        Guid? ownerClientId,
        CancellationToken ct = default,
        float? reviewTemperature = null,
        bool shouldUpdateReviewTemperature = false,
        IReadOnlyList<CrawlRepoFilterDto>? repoFilters = null)
    {
        return await this.UpdateWithResultAsync(
            configId, crawlIntervalSeconds, isActive, ownerClientId, ct,
            reviewTemperature, shouldUpdateReviewTemperature, repoFilters).ConfigureAwait(false) == CrawlConfigurationUpdateResult.Updated;
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationUpdateResult> UpdateWithResultAsync(
        Guid configId,
        int? crawlIntervalSeconds,
        bool? isActive,
        Guid? ownerClientId,
        CancellationToken ct = default,
        float? reviewTemperature = null,
        bool shouldUpdateReviewTemperature = false,
        IReadOnlyList<CrawlRepoFilterDto>? repoFilters = null)
    {
        var owner = await dbContext.CrawlConfigurations.AsNoTracking()
            .Where(c => c.Id == configId && (!ownerClientId.HasValue || c.ClientId == ownerClientId.Value))
            .Select(c => new { c.ClientId, Canonical = c.RepoFilters.Any(f => f.CanonicalSourceRef != null) })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner is null)
        {
            return CrawlConfigurationUpdateResult.NotFound;
        }

        var requiresAdmission = owner.Canonical || repoFilters?.Any(f => f.CanonicalSourceRef is not null) == true;
        await using var admission = requiresAdmission
            ? await this.AcquireReviewTargetAdmissionAsync(owner.ClientId, ct).ConfigureAwait(false)
            : null;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await this.LockCurrentConfigurationAsync(configId, ownerClientId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return CrawlConfigurationUpdateResult.NotFound;
        }

        if (isActive == true && current.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            return CrawlConfigurationUpdateResult.Conflict;
        }

        var canonical = await dbContext.CrawlRepoFilters.AnyAsync(f => f.CrawlConfigurationId == configId && f.CanonicalSourceRef != null, ct)
            .ConfigureAwait(false);
        var missingCanonicalAdmission = canonical && admission is null;
        var replacingRemovedFilters = canonical && repoFilters is not null && current.ReviewTargetLifecycle == ReviewTargetLifecycle.Removed;
        if (missingCanonicalAdmission || replacingRemovedFilters)
        {
            return CrawlConfigurationUpdateResult.Conflict;
        }

        var record = await this.SynchronizeTrackedConfigurationAsync(current, ct).ConfigureAwait(false);
        if (repoFilters is not null)
        {
            await this.ReloadRepoFiltersAsync(record, ct).ConfigureAwait(false);
            await this.ValidateRepositoryReplacementAsync(record, repoFilters, ct);
            this.ReplaceRepoFilters(record, repoFilters);
        }

        ApplyConfigurationSettings(record, crawlIntervalSeconds, isActive, reviewTemperature, shouldUpdateReviewTemperature);
        record.ReviewTargetRevision++;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return CrawlConfigurationUpdateResult.Updated;
    }

    private Task<CrawlConfigurationRecord?> LockCurrentConfigurationAsync(Guid configId, Guid? ownerClientId, CancellationToken ct)
    {
        var query = ownerClientId.HasValue
            ? dbContext.CrawlConfigurations.FromSqlInterpolated(
                $"SELECT * FROM crawl_configurations WHERE id = {configId} AND client_id = {ownerClientId.Value} FOR UPDATE")
            : dbContext.CrawlConfigurations.FromSqlInterpolated($"SELECT * FROM crawl_configurations WHERE id = {configId} FOR UPDATE");
        return query.AsNoTracking().SingleOrDefaultAsync(ct);
    }

    private async Task<CrawlConfigurationRecord> SynchronizeTrackedConfigurationAsync(CrawlConfigurationRecord current, CancellationToken ct)
    {
        var record = dbContext.CrawlConfigurations.Local.SingleOrDefault(c => c.Id == current.Id);
        if (record is not null)
        {
            await dbContext.Entry(record).ReloadAsync(ct).ConfigureAwait(false);
        }
        else
        {
            record = current;
            dbContext.CrawlConfigurations.Attach(record);
        }

        return record;
    }

    private async Task ReloadRepoFiltersAsync(CrawlConfigurationRecord record, CancellationToken ct)
    {
        foreach (var filter in record.RepoFilters.ToList())
        {
            dbContext.Entry(filter).State = EntityState.Detached;
        }

        record.RepoFilters.Clear();
        dbContext.Entry(record).Collection(c => c.RepoFilters).IsLoaded = false;
        await dbContext.Entry(record).Collection(c => c.RepoFilters).LoadAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyConfigurationSettings(
        CrawlConfigurationRecord record, int? crawlIntervalSeconds, bool? isActive, float? reviewTemperature, bool shouldUpdateReviewTemperature)
    {
        if (crawlIntervalSeconds.HasValue)
        {
            record.CrawlIntervalSeconds = crawlIntervalSeconds.Value;
        }

        if (isActive.HasValue)
        {
            record.IsActive = isActive.Value;
        }

        if (shouldUpdateReviewTemperature)
        {
            record.ReviewTemperature = reviewTemperature;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRepoFiltersAsync(
        Guid configId,
        IReadOnlyList<CrawlRepoFilterDto> filters,
        CancellationToken ct = default)
    {
        return await this.UpdateAsync(configId, null, null, null, ct, repoFilters: filters).ConfigureAwait(false);
    }

    private async Task ValidateRepositoryReplacementAsync(CrawlConfigurationRecord config, IReadOnlyList<CrawlRepoFilterDto> filters, CancellationToken ct)
    {
        if (config.RepositoryId is not null)
        {
            if (filters.Count != 1 || filters[0].CanonicalSourceRef is not { } reference)
            {
                throw new InvalidOperationException("A repository-specific configuration requires one canonical repository filter for its provider.");
            }

            if (string.IsNullOrWhiteSpace(reference.Value) || !string.Equals(
                    reference.Provider, config.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A repository-specific configuration requires one canonical repository filter for its provider.");
            }

            var repositoryKey = reference.Value.ToLowerInvariant();
            var branchFilter = config.BranchFilter ?? string.Empty;
            var candidates = await dbContext.CrawlConfigurations.Where(other => other.Id != config.Id && other.ClientId == config.ClientId &&
                                                                                ((other.OrganizationUrl == config.OrganizationUrl &&
                                                                                  other.ProjectId == config.ProjectId &&
                                                                                  other.RepositoryId == reference.Value &&
                                                                                  (other.BranchFilter ?? string.Empty) == branchFilter) ||
                                                                                 (other.Provider == config.Provider && other.RepoFilters.Count == 1 &&
                                                                                  other.RepoFilters.Any(filter =>
                                                                                      filter.CanonicalSourceRef != null &&
                                                                                      filter.CanonicalSourceRef.ToLower() == repositoryKey &&
                                                                                      filter.SourceProvider != null))))
                .Select(other => new { other.OrganizationUrl, other.ProjectId })
                .ToListAsync(ct);
            var conflict = candidates.Any(other =>
                string.Equals(other.OrganizationUrl.TrimEnd('/'), config.OrganizationUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.ProjectId, config.ProjectId, StringComparison.OrdinalIgnoreCase));
            if (conflict)
            {
                throw new InvalidOperationException("A review target already exists for the selected repository.");
            }
        }
    }

    private void ReplaceRepoFilters(CrawlConfigurationRecord config, IReadOnlyList<CrawlRepoFilterDto> filters)
    {
        if (config.RepositoryId is not null)
        {
            config.RepositoryId = filters[0].CanonicalSourceRef!.Value;
        }

        // Full-replacement semantics: remove all existing filters, then insert new ones.
        dbContext.CrawlRepoFilters.RemoveRange(config.RepoFilters);

        foreach (var filter in filters)
        {
            config.RepoFilters.Add(
                new CrawlRepoFilterRecord
                {
                    Id = Guid.NewGuid(),
                    CrawlConfigurationId = config.Id,
                    SourceProvider = filter.CanonicalSourceRef?.Provider,
                    CanonicalSourceRef = filter.CanonicalSourceRef?.Value,
                    DisplayName = filter.DisplayName,
                    RepositoryName = filter.RepositoryName,
                    TargetBranchPatterns = filter.TargetBranchPatterns.ToArray(),
                });
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSourceScopeAsync(
        Guid configId,
        ProCursorSourceScopeMode scopeMode,
        IReadOnlyList<Guid> proCursorSourceIds,
        CancellationToken ct = default)
    {
        var config = await dbContext.CrawlConfigurations
            .Include(c => c.ProCursorSources)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
        if (config is null)
        {
            return false;
        }

        config.ProCursorSourceScopeMode = scopeMode;
        dbContext.CrawlConfigurationProCursorSources.RemoveRange(config.ProCursorSources);

        if (scopeMode == ProCursorSourceScopeMode.SelectedSources)
        {
            foreach (var sourceId in proCursorSourceIds.Distinct())
            {
                config.ProCursorSources.Add(
                    new CrawlConfigurationProCursorSourceRecord
                    {
                        CrawlConfigurationId = configId,
                        ProCursorSourceId = sourceId,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static CrawlConfigurationDto ToDto(CrawlConfigurationRecord c)
    {
        var proCursorSourceIds = c.ProCursorSources
            .Select(link => link.ProCursorSourceId)
            .Distinct()
            .ToList()
            .AsReadOnly();

        var invalidProCursorSourceIds = c.ProCursorSources
            .Where(link => link.ProCursorSource is null || link.ProCursorSource.ClientId != c.ClientId ||
                           !link.ProCursorSource.IsEnabled)
            .Select(link => link.ProCursorSourceId)
            .Distinct()
            .ToList()
            .AsReadOnly();

        return new CrawlConfigurationDto(
            c.Id,
            c.ClientId,
            c.Provider,
            c.OrganizationUrl,
            c.ProjectId,
            c.CrawlIntervalSeconds,
            c.IsActive,
            c.CreatedAt,
            c.RepoFilters
                .Select(f => new CrawlRepoFilterDto(
                    f.Id,
                    f.RepositoryName,
                    f.TargetBranchPatterns,
                    f.SourceProvider is not null && f.CanonicalSourceRef is not null
                        ? new CanonicalSourceReferenceDto(f.SourceProvider, f.CanonicalSourceRef)
                        : null,
                    f.DisplayName))
                .ToList()
                .AsReadOnly(),
            c.OrganizationScopeId,
            c.ProCursorSourceScopeMode,
            proCursorSourceIds,
            invalidProCursorSourceIds,
            c.ReviewTemperature,
            c.ReviewTargetLifecycle,
            c.ReviewTargetRevision);
    }

    private IQueryable<CrawlConfigurationRecord> BaseQuery()
    {
        return dbContext.CrawlConfigurations
            .Include(c => c.Client)
            .Include(c => c.RepoFilters)
            .Include(c => c.ProCursorSources)
            .ThenInclude(link => link.ProCursorSource);
    }
}
