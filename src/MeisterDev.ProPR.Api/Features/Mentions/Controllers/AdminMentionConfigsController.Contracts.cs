// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>A mention scanning configuration as returned by the admin API.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">The client that answers, and that pays for the answers.</param>
/// <param name="Provider">Normalized source-control provider family.</param>
/// <param name="ProviderScopePath">Provider scope path the project lives under.</param>
/// <param name="ProviderProjectKey">Provider project, workspace, or namespace key.</param>
/// <param name="ScanIntervalSeconds">Shortest gap between two scans of this configuration.</param>
/// <param name="IsActive">Whether this configuration is scanned.</param>
/// <param name="CreatedAt">When the configuration was created.</param>
/// <param name="RepoFilters">The repositories answered on.</param>
public sealed record MentionConfigResponse(
    Guid Id,
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    int ScanIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MentionRepoFilterResponse> RepoFilters);

/// <summary>One repository a mention configuration answers on.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="RepositoryId">Provider-native repository identifier. What the scan matches on.</param>
/// <param name="DisplayName">Human-readable repository name, for display only.</param>
/// <param name="CanonicalSourceRef">Provider-aware canonical source reference, when available.</param>
/// <param name="SourceProvider">Provider key backing the guided repository selection, when one was used.</param>
public sealed record MentionRepoFilterResponse(
    Guid Id,
    string RepositoryId,
    string? DisplayName,
    string? CanonicalSourceRef,
    string? SourceProvider);

/// <summary>Request to declare that a client answers mentions on repositories in one project.</summary>
/// <param name="ClientId">The client that answers.</param>
/// <param name="Provider">Normalized source-control provider family.</param>
/// <param name="ProviderScopePath">Provider scope path the project lives under.</param>
/// <param name="ProviderProjectKey">Provider project, workspace, or namespace key.</param>
/// <param name="RepoFilters">The repositories to answer on. At least one is required.</param>
/// <param name="ScanIntervalSeconds">Shortest gap between two scans. Defaults to 60 seconds.</param>
public sealed record CreateMentionConfigRequest(
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    IReadOnlyList<MentionRepoFilterRequest> RepoFilters,
    int? ScanIntervalSeconds = null);

/// <summary>Request to change a mention configuration. Omitted fields are left as they are.</summary>
/// <param name="ScanIntervalSeconds">New scan interval, or null to leave it.</param>
/// <param name="IsActive">New active flag, or null to leave it.</param>
/// <param name="RepoFilters">Replacement repository list, or null to leave it. Must not be empty when given.</param>
public sealed record PatchMentionConfigRequest(
    int? ScanIntervalSeconds = null,
    bool? IsActive = null,
    IReadOnlyList<MentionRepoFilterRequest>? RepoFilters = null);

/// <summary>One repository in a create or patch request.</summary>
/// <param name="RepositoryId">Provider-native repository identifier.</param>
/// <param name="DisplayName">Human-readable repository name, stored for display only.</param>
/// <param name="CanonicalSourceRef">Provider-aware canonical source reference, when the selection produced one.</param>
/// <param name="SourceProvider">Provider key backing the guided repository selection.</param>
public sealed record MentionRepoFilterRequest(
    string? RepositoryId,
    string? DisplayName = null,
    string? CanonicalSourceRef = null,
    string? SourceProvider = null);
