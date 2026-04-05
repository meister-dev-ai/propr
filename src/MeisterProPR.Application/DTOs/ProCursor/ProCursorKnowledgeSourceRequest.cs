// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Request used to register a new ProCursor knowledge source.
/// </summary>
public sealed record ProCursorKnowledgeSourceRegistrationRequest(
    string DisplayName,
    ProCursorSourceKind SourceKind,
    string? OrganizationUrl,
    string ProjectId,
    string? RepositoryId,
    string DefaultBranch,
    string? RootPath,
    string SymbolMode,
    IReadOnlyList<ProCursorTrackedBranchCreateRequest> TrackedBranches,
    Guid? OrganizationScopeId = null,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? SourceDisplayName = null);

/// <summary>
///     API request body for creating a ProCursor knowledge source.
/// </summary>
public sealed record ProCursorKnowledgeSourceRequest(
    string DisplayName,
    ProCursorSourceKind SourceKind,
    string? OrganizationUrl,
    string ProjectId,
    string? RepositoryId,
    string DefaultBranch,
    string? RootPath,
    string SymbolMode,
    IReadOnlyList<ProCursorTrackedBranchRequest> TrackedBranches,
    Guid? OrganizationScopeId = null,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? SourceDisplayName = null);

/// <summary>
///     Request used to create a tracked branch beneath a ProCursor knowledge source.
/// </summary>
public sealed record ProCursorTrackedBranchCreateRequest(
    string BranchName,
    ProCursorRefreshTriggerMode RefreshTriggerMode,
    bool MiniIndexEnabled = true);

/// <summary>
///     API request body for creating a tracked branch.
/// </summary>
public sealed record ProCursorTrackedBranchRequest(
    string BranchName,
    ProCursorRefreshTriggerMode RefreshTriggerMode,
    bool MiniIndexEnabled = true);

/// <summary>
///     Request used to update refresh behavior for one tracked branch.
/// </summary>
public sealed record ProCursorTrackedBranchUpdateRequest(
    ProCursorRefreshTriggerMode? RefreshTriggerMode = null,
    bool? MiniIndexEnabled = null,
    bool? IsEnabled = null);

/// <summary>
///     API request body for patching one tracked branch.
/// </summary>
public sealed record ProCursorTrackedBranchPatchRequest(
    ProCursorRefreshTriggerMode? RefreshTriggerMode = null,
    bool? MiniIndexEnabled = null,
    bool? IsEnabled = null);
