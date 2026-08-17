// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes <see cref="ReviewPrScanThread.LastSeenReplyCount" />, and nothing else.
/// </summary>
public interface IReviewPrScanThreadReplyCountWriter
{
    /// <summary>
    ///     Sets the last-seen non-reviewer comment count on the named threads, merging by thread id.
    ///     Threads absent from the map keep their count, and no other column is written.
    ///     Does nothing when the pull request has no scan record.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="organizationUrl">The host that issued the repository identifier.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="replyCountByThreadId">The count to store, keyed by thread id.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetLastSeenReplyCountsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyDictionary<string, int> replyCountByThreadId,
        CancellationToken ct = default);
}
