// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Supplies neutral internal discovery coordinates through registered native capabilities.</summary>
public interface IProviderAdminDiscoveryService
{
    ScmProvider Provider { get; }
    Task<ClientScmScopeDto?> GetScopeAsync(Guid clientId, Guid scopeId, CancellationToken ct = default, Guid? connectionId = null);

    Task<IReadOnlyList<ScmDiscoveryProjectOption>> ListProjectOptionsAsync(
        Guid clientId, Guid scopeId, CancellationToken ct = default, Guid? connectionId = null);

    Task<IReadOnlyList<ScmDiscoverySourceOption>> ListSourceOptionsAsync(
        Guid clientId, Guid scopeId, string projectId,
        ProCursorSourceKind sourceKind, CancellationToken ct = default, Guid? connectionId = null);

    Task<IReadOnlyList<ScmDiscoveryBranchOption>> ListBranchOptionsAsync(
        Guid clientId, Guid scopeId, string projectId,
        ProCursorSourceKind sourceKind, CanonicalSourceReferenceDto reference, CancellationToken ct = default, Guid? connectionId = null);

    Task<IReadOnlyList<ScmDiscoveryCrawlFilterOption>> ListCrawlFilterOptionsAsync(
        Guid clientId, Guid scopeId,
        string projectId, CancellationToken ct = default, Guid? connectionId = null);

    Task<GuidedSourceSelection> ResolveGuidedSourceAsync(
        Guid clientId, Guid scopeId, string projectId,
        ProCursorSourceKind sourceKind, CanonicalSourceReferenceDto? reference, CancellationToken ct = default, Guid? connectionId = null);
}

/// <summary>Saved scope and project coordinates returned by a discovery capability.</summary>
public sealed record ScmDiscoveryProjectOption(Guid ScopeId, string ProjectId, string ProjectName);

public sealed record ScmDiscoverySourceOption(
    string SourceKind,
    CanonicalSourceReferenceDto CanonicalSourceRef,
    string DisplayName,
    string? DefaultBranch);

public sealed record ScmDiscoveryBranchOption(string BranchName, bool IsDefault);

public sealed record ScmDiscoveryCrawlFilterOption(
    CanonicalSourceReferenceDto CanonicalSourceRef,
    string DisplayName,
    IReadOnlyList<ScmDiscoveryBranchOption> BranchSuggestions);
