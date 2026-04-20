// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Transitional provider-backed admin discovery seam for guided configuration flows.
///     The current DTO surface remains Azure DevOps-shaped because the guided admin endpoints are still Azure-specific.
/// </summary>
public interface IProviderAdminDiscoveryService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Resolves one persisted provider scope selection for guided admin flows.</summary>
    Task<ClientScmScopeDto?> GetScopeAsync(
        Guid clientId,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>Lists projects available in the selected provider scope.</summary>
    Task<IReadOnlyList<AdoProjectOptionDto>> ListProjectsAsync(
        Guid clientId,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>Lists sources available in the selected project.</summary>
    Task<IReadOnlyList<AdoSourceOptionDto>> ListSourcesAsync(
        Guid clientId,
        Guid scopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CancellationToken ct = default);

    /// <summary>Lists branches available for the selected source.</summary>
    Task<IReadOnlyList<AdoBranchOptionDto>> ListBranchesAsync(
        Guid clientId,
        Guid scopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CanonicalSourceReferenceDto canonicalSourceRef,
        CancellationToken ct = default);

    /// <summary>Lists source options and branch suggestions suitable for crawl-filter configuration.</summary>
    Task<IReadOnlyList<AdoCrawlFilterOptionDto>> ListCrawlFiltersAsync(
        Guid clientId,
        Guid scopeId,
        string projectId,
        CancellationToken ct = default);
}
