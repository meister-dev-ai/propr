// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Fetches the current status and comment history of all threads ProPR owns on a pull request.
///     Used by <c>PrCrawlService</c> to drive the per-thread lifecycle state machine.
/// </summary>
public interface IReviewerThreadStatusFetcher
{
    /// <summary>
    ///     Returns a projection of every thread on the given pull request that ProPR owns.
    ///     Includes the current provider status, anchored file path, and the comment history
    ///     (all non-system comments concatenated chronologically, truncated to a configurable max length).
    /// </summary>
    /// <param name="ownership">
    ///     The pass's ownership resolver, built once from the pull request's provenance. The adapter adds the
    ///     identity its own connection handshake resolves and asks this resolver rather than testing ownership
    ///     itself, so every provider and every consumer agree on which threads are ProPR's.
    /// </param>
    Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        ThreadOwnershipResolver ownership,
        Guid clientId,
        CancellationToken ct = default);
}
