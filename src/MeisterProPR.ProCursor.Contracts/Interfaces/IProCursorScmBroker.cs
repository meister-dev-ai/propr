// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Service-side broker for ProPR-managed SCM access needed by the extracted ProCursor runtime.
/// </summary>
public interface IProCursorScmBroker
{
    /// <summary>
    ///     Resolves the latest observed commit SHA for one tracked branch.
    /// </summary>
    Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSourceDto source,
        ProCursorTrackedBranchDto trackedBranch,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves materializable text files for one tracked source state.
    /// </summary>
    Task<ProCursorScmMaterializationResponse> MaterializeAsync(
        ProCursorKnowledgeSourceDto source,
        ProCursorTrackedBranchDto trackedBranch,
        string? requestedCommitSha,
        CancellationToken ct = default);
}
