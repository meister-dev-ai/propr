// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Ports;

/// <summary>Abstraction for signalling that a newly-submitted review job is ready for execution.</summary>
public interface IReviewExecutionQueue
{
    /// <summary>Signals that the given job should be picked up by review execution.</summary>
    Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default);
}
