// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Azure DevOps-backed tracked-branch head detector for ProCursor scheduling.
/// </summary>
public sealed partial class AdoTrackedBranchChangeDetector(
    IProCursorScmBroker scmBroker,
    ILogger<AdoTrackedBranchChangeDetector> logger) : IProCursorTrackedBranchChangeDetector
{
    /// <summary>
    ///     Gets the latest commit SHA for a tracked branch.
    /// </summary>
    /// <param name="source">The ProCursor knowledge source.</param>
    /// <param name="trackedBranch">The tracked branch to get the commit SHA for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The commit SHA if found; otherwise null.</returns>
    public async Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default)
    {
        try
        {
            return await scmBroker.GetLatestCommitShaAsync(ToDto(source), ToDto(trackedBranch), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            LogBranchHeadResolutionFailed(logger, source.Id, trackedBranch.Id, trackedBranch.BranchName, ex);
            return null;
        }
    }

    private static ProCursorKnowledgeSourceDto ToDto(ProCursorKnowledgeSource source)
    {
        return new ProCursorKnowledgeSourceDto(
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
            source.TrackedBranches.Select(ToDto).ToList().AsReadOnly(),
            source.OrganizationScopeId,
            string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) || string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
                ? null
                : new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue),
            source.SourceDisplayName);
    }

    private static ProCursorTrackedBranchDto ToDto(ProCursorTrackedBranch trackedBranch)
    {
        return new ProCursorTrackedBranchDto(
            trackedBranch.Id,
            trackedBranch.BranchName,
            trackedBranch.RefreshTriggerMode,
            trackedBranch.MiniIndexEnabled,
            trackedBranch.LastSeenCommitSha,
            trackedBranch.LastIndexedCommitSha,
            trackedBranch.IsEnabled,
            "unknown");
    }
}
