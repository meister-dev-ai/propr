// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Data transfer object for a mention scanning configuration.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">The client that answers, and that pays for the answers.</param>
/// <param name="Provider">Normalized source-control provider family.</param>
/// <param name="ProviderScopePath">Provider scope path the project lives under.</param>
/// <param name="ProviderProjectKey">Provider project, workspace, or namespace key.</param>
/// <param name="ScanIntervalSeconds">Shortest gap between two scans of this configuration.</param>
/// <param name="IsActive">Whether this configuration is scanned.</param>
/// <param name="CreatedAt">When the configuration was created.</param>
/// <param name="RepoFilters">The repositories answered on. Never empty.</param>
public sealed record MentionConfigurationDto(
    Guid Id,
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    int ScanIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MentionRepoFilterDto> RepoFilters);

/// <summary>Data transfer object for one repository a mention configuration answers on.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="RepositoryId">Provider-native repository identifier. What the scan matches on.</param>
/// <param name="CanonicalSourceRef">Provider-aware canonical source reference, when available.</param>
/// <param name="DisplayName">Human-readable repository name, for display only.</param>
/// <param name="SourceProvider">Provider key backing the guided repository selection, when one was used.</param>
/// <param name="ClaimedAt">When the repository was claimed. Comments published before it are never answered.</param>
public sealed record MentionRepoFilterDto(
    Guid Id,
    string RepositoryId,
    string? CanonicalSourceRef = null,
    string? DisplayName = null,
    string? SourceProvider = null,
    DateTimeOffset? ClaimedAt = null);
