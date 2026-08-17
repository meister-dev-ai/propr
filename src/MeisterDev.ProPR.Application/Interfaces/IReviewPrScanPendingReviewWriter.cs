// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes the declined revision, <see cref="ReviewPrScan.PendingReviewRevisionKey" />, and nothing else.
///     A holder of this port cannot reach either watermark or any per-thread progress.
/// </summary>
public interface IReviewPrScanPendingReviewWriter
{
    /// <summary>
    ///     Records that a pull request sits at a revision an automatic trigger declined to review. Creates the
    ///     scan record when the pull request has none, with both watermarks left empty, because declining a
    ///     revision is not a record of having processed one.
    /// </summary>
    /// <remarks>
    ///     The detection timestamp is stamped only when the revision differs from the one already recorded, so
    ///     a crawl that keeps re-declining the same revision reports how long the pull request has been
    ///     waiting rather than how recently it was last looked at.
    /// </remarks>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="organizationUrl">The host that issued the repository identifier.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="revisionKey">The stored revision key that was declined.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetPendingReviewRevisionAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default);
}
