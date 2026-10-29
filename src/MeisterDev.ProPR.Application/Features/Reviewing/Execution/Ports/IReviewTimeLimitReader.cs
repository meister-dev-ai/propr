// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reads the maximum review duration of a job's tenant and how much of it is left for the current execution of
///     the job.
/// </summary>
public interface IReviewTimeLimitReader
{
    /// <summary>
    ///     Reads the maximum review duration of the job's tenant and the time left until the deadline, which is the
    ///     start of the current execution plus that duration. The store measures the time left on the clock that
    ///     stamped the start, so the clock of the host that asks does not have to agree with it.
    /// </summary>
    /// <param name="jobId">The review job.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    ///     The reading, or null when the tenant sets no maximum review duration or the job has not started
    ///     processing.
    /// </returns>
    Task<ReviewTimeLimitReading?> ReadAsync(Guid jobId, CancellationToken ct = default);
}

/// <summary>The maximum review duration of a job's tenant, as it applies to the current execution of the job.</summary>
/// <param name="MaxReviewDurationSeconds">The maximum review duration of the tenant.</param>
/// <param name="Remaining">
///     The time left until the deadline when the reading was taken. Zero or negative when the deadline has passed.
/// </param>
public sealed record ReviewTimeLimitReading(int MaxReviewDurationSeconds, TimeSpan Remaining);
