// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     No-op change detector used when Azure DevOps integrations are stubbed.
/// </summary>
public sealed class NullProCursorTrackedBranchChangeDetector : IProCursorTrackedBranchChangeDetector
{
    /// <summary>
    ///     Gets the latest commit SHA for a tracked branch.
    /// </summary>
    /// <param name="source">The knowledge source.</param>
    /// <param name="trackedBranch">The tracked branch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Always returns null as this is a no-op implementation.</returns>
    public Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }
}
