// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs.ProCursor;

/// <summary>
///     Client-scoped ProCursor source descriptor returned by the module facade.
/// </summary>
public sealed record ProCursorKnowledgeSourceDto(
    Guid Id,
    Guid ClientId,
    string DisplayName,
    ProCursorSourceKind SourceKind,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string DefaultBranch,
    string? RootPath,
    bool IsEnabled,
    string SymbolMode,
    ProCursorSnapshotDto? LatestSnapshot,
    IReadOnlyList<ProCursorTrackedBranchDto> TrackedBranches,
    Guid? OrganizationScopeId = null,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? SourceDisplayName = null);

/// <summary>
///     One tracked branch configured for a ProCursor knowledge source.
/// </summary>
public sealed record ProCursorTrackedBranchDto(
    Guid Id,
    string BranchName,
    ProCursorRefreshTriggerMode RefreshTriggerMode,
    bool MiniIndexEnabled,
    string? LastSeenCommitSha,
    string? LastIndexedCommitSha,
    bool IsEnabled,
    string FreshnessStatus);
