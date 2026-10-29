// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The limits a tenant sets on how its reviews run: how many of its review jobs may be processing at once, and
///     how long one review may run. A null value means that the tenant sets no limit of its own, and the limits of
///     the installation apply alone.
/// </summary>
/// <param name="MaxConcurrentReviews">
///     How many review jobs of the tenant may be processing at once, in process or on a runner, or null. Thread
///     passes and mention replies are not counted.
/// </param>
/// <param name="MaxReviewDurationSeconds">
///     How many seconds after the start of processing a review stops starting further files and completes with
///     what it has reviewed, or null.
/// </param>
public sealed record TenantReviewExecutionLimits(int? MaxConcurrentReviews, int? MaxReviewDurationSeconds)
{
    /// <summary>The smallest number of concurrent reviews a tenant can set.</summary>
    public const int MinimumConcurrentReviews = 1;

    /// <summary>The shortest maximum review duration a tenant can set, in seconds.</summary>
    public const int MinimumReviewDurationSeconds = 60;

    /// <summary>The longest maximum review duration a tenant can set, in seconds, which is one day.</summary>
    public const int MaximumReviewDurationSeconds = 86_400;

    /// <summary>The limits of a tenant that sets none.</summary>
    public static TenantReviewExecutionLimits None { get; } = new(null, null);

    /// <summary>
    ///     Throws <see cref="ArgumentOutOfRangeException" /> when a value is outside the range a tenant can set.
    /// </summary>
    public void EnsureValid()
    {
        if (this.MaxConcurrentReviews is < MinimumConcurrentReviews)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MaxConcurrentReviews),
                this.MaxConcurrentReviews,
                $"The number of concurrent reviews must be at least {MinimumConcurrentReviews}.");
        }

        if (this.MaxReviewDurationSeconds is < MinimumReviewDurationSeconds or > MaximumReviewDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MaxReviewDurationSeconds),
                this.MaxReviewDurationSeconds,
                $"The maximum review duration must be from {MinimumReviewDurationSeconds} to {MaximumReviewDurationSeconds} seconds.");
        }
    }
}
