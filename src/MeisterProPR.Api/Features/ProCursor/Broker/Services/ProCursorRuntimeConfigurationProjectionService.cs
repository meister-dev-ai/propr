// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Services;

/// <summary>
///     Builds runtime configuration projections that ProCursor can hydrate into its in-memory cache.
/// </summary>
public sealed class ProCursorRuntimeConfigurationProjectionService(IProCursorKnowledgeSourceRepository knowledgeSourceRepository)
{
    /// <summary>
    ///     Lists enabled runtime-configuration projections for ProCursor.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The enabled runtime-configuration projections.</returns>
    public async Task<IReadOnlyList<ProCursorRuntimeConfigurationProjectionDto>> ListEnabledAsync(CancellationToken ct = default)
    {
        var sources = await knowledgeSourceRepository.ListEnabledAsync(ct);
        return sources
            .Select(MapProjection)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Rebuilds the runtime-configuration projection for a specific source.
    /// </summary>
    /// <param name="sourceId">ProCursor source identifier.</param>
    /// <param name="request">Refresh request payload.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The refreshed runtime-configuration projection.</returns>
    public async Task<ProCursorRuntimeConfigurationProjectionDto> RefreshAsync(
        Guid sourceId,
        ProCursorRuntimeConfigurationRefreshRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await knowledgeSourceRepository.GetBySourceIdAsync(sourceId, ct)
                     ?? throw new KeyNotFoundException($"ProCursor source {sourceId} was not found.");

        if (!source.IsEnabled)
        {
            throw new InvalidOperationException($"ProCursor source {sourceId} is disabled.");
        }

        if (source.TrackedBranches.Count == 0)
        {
            throw new InvalidOperationException($"ProCursor source {sourceId} has no tracked branches.");
        }

        return MapProjection(source);
    }

    private static ProCursorRuntimeConfigurationProjectionDto MapProjection(ProCursorKnowledgeSource source)
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        return new ProCursorRuntimeConfigurationProjectionDto(
            ComputeProjectionVersion(source),
            fetchedAt,
            new ProCursorKnowledgeSourceDto(
                source.Id,
                source.ClientId,
                source.DisplayName,
                source.SourceKind,
                source.ProviderScopePath,
                source.ProviderProjectKey,
                source.RepositoryId,
                source.DefaultBranch,
                source.RootPath,
                source.IsEnabled,
                source.SymbolMode,
                null,
                source.TrackedBranches
                    .OrderBy(branch => branch.BranchName, StringComparer.OrdinalIgnoreCase)
                    .Select(MapTrackedBranch)
                    .ToList()
                    .AsReadOnly(),
                source.OrganizationScopeId,
                string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) || string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
                    ? null
                    : new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue),
                source.SourceDisplayName));
    }

    private static ProCursorTrackedBranchDto MapTrackedBranch(ProCursorTrackedBranch branch)
    {
        return new ProCursorTrackedBranchDto(
            branch.Id,
            branch.BranchName,
            branch.RefreshTriggerMode,
            branch.MiniIndexEnabled,
            branch.LastSeenCommitSha,
            branch.LastIndexedCommitSha,
            branch.IsEnabled,
            "unknown");
    }

    private static string ComputeProjectionVersion(ProCursorKnowledgeSource source)
    {
        var payload = string.Join(
            "|",
            source.Id.ToString("D"),
            source.ClientId.ToString("D"),
            source.DisplayName,
            source.SourceKind,
            source.ProviderScopePath,
            source.ProviderProjectKey,
            source.RepositoryId,
            source.DefaultBranch,
            source.RootPath ?? string.Empty,
            source.IsEnabled,
            source.SymbolMode,
            source.OrganizationScopeId?.ToString("D") ?? string.Empty,
            source.CanonicalSourceProvider ?? string.Empty,
            source.CanonicalSourceValue ?? string.Empty,
            source.SourceDisplayName ?? string.Empty,
            source.UpdatedAt.UtcTicks,
            string.Join(
                ";",
                source.TrackedBranches
                    .OrderBy(branch => branch.Id)
                    .Select(branch => string.Join(
                        ",",
                        branch.Id.ToString("D"),
                        branch.BranchName,
                        branch.RefreshTriggerMode,
                        branch.MiniIndexEnabled,
                        branch.LastSeenCommitSha ?? string.Empty,
                        branch.LastIndexedCommitSha ?? string.Empty,
                        branch.IsEnabled,
                        branch.UpdatedAt.UtcTicks))));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
