// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Discovers client-scoped Azure DevOps organizations, projects, sources, and branches for guided admin flows.
/// </summary>
public interface IAdoDiscoveryService
{
    /// <summary>Lists projects available in the selected organization scope.</summary>
    Task<IReadOnlyList<AdoProjectOptionDto>> ListProjectsAsync(
        Guid clientId,
        Guid organizationScopeId,
        CancellationToken ct = default);

    /// <summary>Lists repositories or wikis available in the selected project.</summary>
    Task<IReadOnlyList<AdoSourceOptionDto>> ListSourcesAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CancellationToken ct = default);

    /// <summary>Lists branches for the selected repository or wiki source.</summary>
    Task<IReadOnlyList<AdoBranchOptionDto>> ListBranchesAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CanonicalSourceReferenceDto canonicalSourceRef,
        CancellationToken ct = default);

    /// <summary>Lists repository options and branch suggestions for crawl-filter configuration.</summary>
    Task<IReadOnlyList<AdoCrawlFilterOptionDto>> ListCrawlFiltersAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        CancellationToken ct = default);
}
