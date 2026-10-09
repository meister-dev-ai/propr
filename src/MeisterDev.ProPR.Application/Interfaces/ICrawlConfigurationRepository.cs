// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Repository for per-client provider crawl configurations.</summary>
public interface ICrawlConfigurationRepository
{
    /// <summary>Serializes customer admission and repository target mutations across cell processes.</summary>
    Task<IAsyncDisposable> AcquireReviewTargetAdmissionAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Reads saved metadata without provider activation or credentials.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetManagementTargetsAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Filters, counts, and pages saved canonical targets in the database.</summary>
    /// <remarks>Requires page 1 or greater, pageSize from 1 to 100, and an offset no greater than Int32.MaxValue. Count, rows and representation version share a repeatable-read snapshot; removed targets are excluded.</remarks>
    Task<CrawlConfigurationPageDto> GetManagementTargetPageAsync(
        Guid clientId, string? search, ScmProvider? provider,
        ReviewTargetLifecycle? lifecycle, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Conditionally changes lifecycle and stops crawling without deleting related records.</summary>
    /// <remarks>Requires a positive expected revision and a defined lifecycle value. Missing, foreign, stale, removed, and non-managed targets return false. Success increments the revision and leaves crawling off. Admission serialization is acquired before the conditional write.</remarks>
    Task<bool> ChangeReviewTargetLifecycleAsync(
        Guid targetId, Guid clientId, long expectedRevision, ReviewTargetLifecycle lifecycle, CancellationToken ct = default);

    /// <summary>Restores a removed target and its destination policy atomically, leaving crawling off.</summary>
    /// <remarks>Requires an exact-one canonical expected filter, valid destination patterns and matching owned removed identity and revision. Invalid patterns are rejected before admission or database writes. Invalid, foreign, stale, or changed identity returns false. Success preserves the target identifier, increments its revision and replaces the policy under admission serialization.</remarks>
    Task<bool> RestoreReviewTargetAsync(
        CrawlConfigurationDto expectedTarget, Guid clientId, IReadOnlyList<string> targetBranchPatterns, CancellationToken ct = default);

    /// <summary>Adds a new crawl configuration for the given client.</summary>
    Task<CrawlConfigurationDto> AddAsync(
        Guid clientId,
        ScmProvider provider,
        string organizationUrl,
        string projectId,
        int crawlIntervalSeconds,
        Guid? organizationScopeId = null,
        CancellationToken ct = default,
        float? reviewTemperature = null);

    /// <summary>Creates an inactive repository-scoped configuration and its canonical filter in one save.</summary>
    Task<CrawlConfigurationDto> AddReviewTargetAsync(
        Guid clientId,
        ScmProvider provider,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        string repositoryName,
        CancellationToken ct = default,
        IReadOnlyList<string>? targetBranchPatterns = null);

    /// <summary>Atomically replaces only a canonical repository target's policy when its authorized identity and stored patterns match.</summary>
    Task<bool> UpdateReviewTargetPolicyAsync(
        CrawlConfigurationDto expectedTarget,
        Guid clientId,
        IReadOnlyList<string> expectedTargetBranchPatterns,
        IReadOnlyList<string> targetBranchPatterns,
        CancellationToken ct = default);

    /// <summary>Deletes a generic crawl configuration. Returns false for missing or foreign configurations, exact-one canonical managed targets, and removed configurations retaining any canonical exclusion.</summary>
    /// <remarks>Managed targets require revision-checked lifecycle removal, which retains identity, policy and review history.</remarks>
    Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default);

    /// <summary>Returns true if a configuration with the same org/project/repo/branch already exists for the client.</summary>
    Task<bool> ExistsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string? repositoryId,
        string? branchFilter,
        CancellationToken ct = default);

    /// <summary>Returns all active crawl configurations across all clients.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>Returns all crawl configurations across all clients, including paused ones.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns all crawl configurations for a specific client.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Enables or disables a crawl configuration. Returns false if not found or not owned by clientId.</summary>
    Task<bool> SetActiveAsync(Guid configId, Guid clientId, bool isActive, CancellationToken ct = default);

    /// <summary>Returns crawl configurations for all specified clients.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientIdsAsync(
        IEnumerable<Guid> clientIds,
        CancellationToken ct = default);

    /// <summary>Returns a single crawl configuration by its own primary-key ID, or <see langword="null" /> if not found.</summary>
    Task<CrawlConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default);

    /// <summary>Returns the persisted policy and activation snapshot for a client-owned target after a policy edit.</summary>
    Task<CrawlConfigurationDto?> GetReviewTargetPolicySnapshotAsync(Guid configId, Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Applies settings and optional replacement repository filters in one persistence operation.
    ///     Returns <see langword="false" /> if missing, not owned by <paramref name="ownerClientId" />, or conflicting with protected state.
    /// </summary>
    Task<bool> UpdateAsync(
        Guid configId,
        int? crawlIntervalSeconds,
        bool? isActive,
        Guid? ownerClientId,
        CancellationToken ct = default,
        float? reviewTemperature = null,
        bool shouldUpdateReviewTemperature = false,
        IReadOnlyList<CrawlRepoFilterDto>? repoFilters = null);

    /// <summary>
    ///     Applies settings and optional replacement repository filters in one persistence operation.
    ///     Distinguishes missing or foreign configurations from lifecycle and admission conflicts.
    /// </summary>
    Task<CrawlConfigurationUpdateResult> UpdateWithResultAsync(
        Guid configId,
        int? crawlIntervalSeconds,
        bool? isActive,
        Guid? ownerClientId,
        CancellationToken ct = default,
        float? reviewTemperature = null,
        bool shouldUpdateReviewTemperature = false,
        IReadOnlyList<CrawlRepoFilterDto>? repoFilters = null);

    /// <summary>
    ///     Replaces all repo filters for the given crawl configuration (full-replacement semantics).
    ///     Pass an empty list to clear all filters. Returns <see langword="false" /> if missing or conflicting with protected state.
    /// </summary>
    Task<bool> UpdateRepoFiltersAsync(
        Guid configId,
        IReadOnlyList<CrawlRepoFilterDto> filters,
        CancellationToken ct = default);

    /// <summary>
    ///     Replaces the explicit ProCursor source scope for the given crawl configuration.
    ///     When <paramref name="scopeMode" /> is <c>AllClientSources</c>, the association set is cleared.
    /// </summary>
    Task<bool> UpdateSourceScopeAsync(
        Guid configId,
        ProCursorSourceScopeMode scopeMode,
        IReadOnlyList<Guid> proCursorSourceIds,
        CancellationToken ct = default);
}
