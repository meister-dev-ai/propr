// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;

/// <summary>Outcome of attempting to stop a review job.</summary>
public enum StopReviewJobOutcome
{
    /// <summary>The job was running or queued and has been marked stopped.</summary>
    Stopped = 0,

    /// <summary>No review job exists for the supplied identifier.</summary>
    NotFound = 1,

    /// <summary>The job has already reached a terminal state and therefore cannot be stopped.</summary>
    AlreadyFinished = 2,
}

/// <summary>Result returned when a review job stop is attempted.</summary>
/// <param name="Outcome">High-level outcome of the stop attempt.</param>
/// <param name="ClientId">Owning client of the job, when it was found.</param>
public sealed record StopReviewJobResult(
    StopReviewJobOutcome Outcome,
    Guid? ClientId = null);
