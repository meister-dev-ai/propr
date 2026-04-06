// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Intake.Runtime;

/// <summary>
///     Current execution-queue adapter for review intake.
///     Pending review jobs are persisted in the database and picked up by <c>ReviewJobWorker</c>,
///     so signalling is presently a no-op boundary that keeps intake decoupled from the worker.
/// </summary>
public sealed class ReviewExecutionQueue : IReviewExecutionQueue
{
    /// <inheritdoc />
    public Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
