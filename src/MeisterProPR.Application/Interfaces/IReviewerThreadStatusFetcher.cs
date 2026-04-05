// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Fetches the current status and comment history of all reviewer-owned threads on a pull request.
///     Used by <c>PrCrawlService</c> to drive the per-thread lifecycle state machine.
/// </summary>
public interface IReviewerThreadStatusFetcher
{
    /// <summary>
    ///     Returns a projection of every reviewer-owned thread on the given pull request.
    ///     Includes the current ADO status, anchored file path, and the comment history
    ///     (all non-system comments concatenated chronologically, truncated to a configurable max length).
    /// </summary>
    Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid clientId,
        CancellationToken ct = default);
}
