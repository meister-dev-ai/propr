// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs.AzureDevOps;

/// <summary>
///     Transport shape for a provider-aware canonical source reference.
/// </summary>
public sealed record CanonicalSourceReferenceDto(string Provider, string Value);

/// <summary>
///     A project option discovered for one organization scope.
/// </summary>
public sealed record AdoProjectOptionDto(Guid OrganizationScopeId, string ProjectId, string ProjectName);

/// <summary>
///     A repository or wiki option discovered for one project.
/// </summary>
public sealed record AdoSourceOptionDto(
    string SourceKind,
    CanonicalSourceReferenceDto CanonicalSourceRef,
    string DisplayName,
    string? DefaultBranch);

/// <summary>
///     A branch option discovered for one source.
/// </summary>
public sealed record AdoBranchOptionDto(string BranchName, bool IsDefault);

/// <summary>
///     A crawl-filter option discovered for one project.
/// </summary>
public sealed record AdoCrawlFilterOptionDto(
    CanonicalSourceReferenceDto CanonicalSourceRef,
    string DisplayName,
    IReadOnlyList<AdoBranchOptionDto> BranchSuggestions);
