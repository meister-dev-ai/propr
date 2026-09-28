// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The state a review job held before it was retired as superseded, kept so a caller whose reason for
///     retiring it fell away can put the job back exactly as it was. Superseding stamps a completion time and
///     clears the lease, so restoring the status alone would leave a restartable job reporting that it
///     completed.
/// </summary>
/// <param name="Status">The status the job held before it was retired.</param>
/// <param name="CompletedAt">The completion time it carried, which is null for a job that had not ended.</param>
/// <param name="LeaseOwner">The lease owner it carried.</param>
/// <param name="LeaseExpiresAt">When that lease was due to expire.</param>
/// <param name="LastHeartbeatAt">When that lease was last renewed.</param>
public sealed record SupersededReviewJobState(
    JobStatus Status,
    DateTimeOffset? CompletedAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? LastHeartbeatAt);
