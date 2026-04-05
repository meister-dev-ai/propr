// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Resolves the latest observed commit for a tracked ProCursor branch.
/// </summary>
public interface IProCursorTrackedBranchChangeDetector
{
    /// <summary>Returns the latest branch-head commit SHA for the configured source branch, or <see langword="null" /> when unavailable.</summary>
    Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default);
}
