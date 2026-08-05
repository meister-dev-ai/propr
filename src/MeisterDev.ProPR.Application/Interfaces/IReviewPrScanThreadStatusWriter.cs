// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes <see cref="ReviewPrScanThread.LastSeenStatus" />, and nothing else.
/// </summary>
public interface IReviewPrScanThreadStatusWriter
{
    /// <summary>
    ///     Sets the last-seen provider status on the named threads, merging by thread id. A <c>null</c>
    ///     value clears the stored status. Threads absent from the map keep their status, and no other
    ///     column is written. Does nothing when the pull request has no scan record.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="statusByThreadId">The status to store, keyed by thread id.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetLastSeenStatusesAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyDictionary<string, string?> statusByThreadId,
        CancellationToken ct = default);
}
