// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes the review watermark, <see cref="ReviewPrScan.LastProcessedCommitId" />, and nothing else.
///     A holder of this port cannot reach any per-thread progress.
/// </summary>
public interface IReviewPrScanWatermarkWriter
{
    /// <summary>
    ///     Sets the last processed revision key for a pull request, leaving every thread row untouched.
    ///     Creates the scan record when the pull request has none: a scan record cannot exist without a
    ///     processed revision, so this is the only operation that brings one into being.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="revisionKey">The stored revision key that has now been processed.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetReviewWatermarkAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default);
}
