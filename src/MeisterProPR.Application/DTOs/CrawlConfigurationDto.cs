// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for a crawl configuration.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="Provider">Source control management provider.</param>
/// <param name="ProviderScopePath">Provider-specific scope path or URL used to enumerate crawl targets.</param>
/// <param name="ProviderProjectKey">Provider-specific project, workspace, or namespace key within the selected scope.</param>
/// <param name="CrawlIntervalSeconds">Polling interval in seconds.</param>
/// <param name="IsActive">Whether the configuration is active.</param>
/// <param name="CreatedAt">When the configuration was created.</param>
/// <param name="RepoFilters">
///     Optional repository-scope filters. Empty list means all repositories are crawled.
/// </param>
/// <param name="OrganizationScopeId">Optional client-scoped organization selector backing the configuration.</param>
/// <param name="ProCursorSourceScopeMode">Whether the crawl uses all client sources or an explicit subset.</param>
/// <param name="ProCursorSourceIds">Explicit ProCursor source IDs selected for this crawl when source scoping is enabled.</param>
/// <param name="InvalidProCursorSourceIds">Selected ProCursor source IDs that are now invalid and need operator repair.</param>
public sealed record CrawlConfigurationDto(
    Guid Id,
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CrawlRepoFilterDto> RepoFilters,
    Guid? OrganizationScopeId = null,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null,
    IReadOnlyList<Guid>? InvalidProCursorSourceIds = null,
    float? ReviewTemperature = null);

/// <summary>Data transfer object for a crawl repository filter entry.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="RepositoryName">Provider repository display name snapshot used for case-insensitive matching.</param>
/// <param name="TargetBranchPatterns">Glob patterns for target branch matching. Empty = all branches.</param>
/// <param name="CanonicalSourceRef">Provider-aware canonical source reference for the selected repository, when available.</param>
/// <param name="DisplayName">Human-readable repository display snapshot used by guided configuration.</param>
public sealed record CrawlRepoFilterDto(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);
