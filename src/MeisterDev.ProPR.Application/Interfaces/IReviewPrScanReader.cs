// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Reads the persisted scan progress for a pull request.
/// </summary>
public interface IReviewPrScanReader
{
    /// <summary>
    ///     Gets the scan record for the given client and pull request,
    ///     or <c>null</c> if no scan has been performed yet.
    ///     The <see cref="ReviewPrScan.Threads" /> collection is included.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<ReviewPrScan?> GetAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);
}
