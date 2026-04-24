// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Ports;

/// <summary>Feature-owned persistence port for review-job intake operations.</summary>
public interface IReviewJobIntakeStore
{
    /// <summary>
    ///     Returns the current active job for the supplied normalized review target and revision,
    ///     or <see langword="null" /> when none exists.
    /// </summary>
    Task<ReviewJob?> FindActiveJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new pending review job for the supplied client and request details.</summary>
    Task<ReviewJob> CreatePendingJobAsync(
        Guid clientId,
        SubmitReviewJobRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single review job by identifier, or <see langword="null" /> if it does not exist.</summary>
    Task<ReviewJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Returns the number of review jobs that are currently pending or processing.</summary>
    Task<int> CountActiveJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the PR context snapshot captured from ADO after the job was created.</summary>
    Task UpdatePrContextAsync(
        Guid jobId,
        string? title,
        string? repositoryName,
        string? sourceBranch,
        string? targetBranch,
        CancellationToken cancellationToken = default);
}
