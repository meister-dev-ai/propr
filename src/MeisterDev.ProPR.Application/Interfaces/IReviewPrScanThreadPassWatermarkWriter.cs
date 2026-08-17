// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes the thread watermark, <see cref="ReviewPrScan.LastThreadPassRevisionKey" />, and nothing else.
///     A holder of this port cannot reach the review watermark or any per-thread progress.
/// </summary>
public interface IReviewPrScanThreadPassWatermarkWriter
{
    /// <summary>
    ///     Sets the revision key the reviewer's threads have now been checked at, leaving the review
    ///     watermark and every thread row untouched. Creates the scan record when the pull request has none,
    ///     with the review watermark left empty, because the thread pass reaches pull requests no file review
    ///     has recorded a revision for and must still be able to record that it has been here.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="organizationUrl">The host that issued the repository identifier.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="revisionKey">The stored revision key the threads have now been checked at.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetThreadPassWatermarkAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default);
}
