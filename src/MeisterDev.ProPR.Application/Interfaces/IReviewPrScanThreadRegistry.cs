// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Writes which <see cref="ReviewPrScanThread" /> rows exist, and none of their columns.
///     Rows appear as a consequence of storing a counter for an unknown thread; this port is how they
///     are taken away again.
/// </summary>
public interface IReviewPrScanThreadRegistry
{
    /// <summary>
    ///     Deletes the thread rows whose thread id is not in the given set. Rows in the set are left
    ///     exactly as they are, including threads the set names that have no row yet.
    ///     Does nothing when the pull request has no scan record.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="organizationUrl">The host that issued the repository identifier.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="threadIds">The thread ids that must keep their row.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task RetainOnlyThreadsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyCollection<string> threadIds,
        CancellationToken ct = default);
}
