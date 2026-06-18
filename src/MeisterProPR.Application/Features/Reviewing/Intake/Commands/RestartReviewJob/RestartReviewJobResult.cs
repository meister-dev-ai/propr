// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;

/// <summary>Outcome of attempting to restart a failed review job.</summary>
public enum RestartReviewJobOutcome
{
    /// <summary>A new pending review job was created and queued.</summary>
    Restarted = 0,

    /// <summary>No review job exists for the supplied identifier.</summary>
    NotFound = 1,

    /// <summary>The review job is not in a failed state and therefore cannot be restarted.</summary>
    NotFailed = 2,

    /// <summary>An active review job already exists for the same pull request revision.</summary>
    DuplicateActiveJob = 3,
}

/// <summary>Result returned when a failed review job restart is attempted.</summary>
/// <param name="Outcome">High-level outcome of the restart attempt.</param>
/// <param name="NewJobId">Identifier of the newly-created job when <see cref="RestartReviewJobOutcome.Restarted" />.</param>
/// <param name="ClientId">Owning client of the source job, when it was found.</param>
public sealed record RestartReviewJobResult(
    RestartReviewJobOutcome Outcome,
    Guid? NewJobId = null,
    Guid? ClientId = null);
