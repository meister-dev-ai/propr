// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persists <see cref="ReviewPrScan" /> watermarks that track the last commit processed
///     for comment resolution on each pull request.
/// </summary>
public interface IReviewPrScanRepository
{
    /// <summary>
    ///     Gets the scan watermark for the given client and pull request,
    ///     or <c>null</c> if no scan has been performed yet.
    ///     The <see cref="ReviewPrScan.Threads" /> collection is included.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">ADO repository identifier.</param>
    /// <param name="pullRequestId">ADO pull request number.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<ReviewPrScan?> GetAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Creates or updates the scan watermark including all child thread records.
    ///     Replaces thread records — any threads no longer present are removed.
    /// </summary>
    /// <param name="record">The scan record to upsert.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task UpsertAsync(ReviewPrScan record, CancellationToken ct = default);
}
