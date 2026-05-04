// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned persistence boundary for executing and finalizing review jobs.
/// </summary>
public interface IReviewJobExecutionStore
{
    /// <summary>
    ///     Gets a review job by its identifier.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <returns>The review job if found; otherwise <c>null</c>.</returns>
    ReviewJob? GetById(Guid id);

    /// <summary>
    ///     Gets all pending review jobs.
    /// </summary>
    /// <returns>A read-only list of pending review jobs.</returns>
    IReadOnlyList<ReviewJob> GetPendingJobs();

    /// <summary>
    ///     Gets review jobs that are stuck in processing state beyond the specified threshold.
    /// </summary>
    /// <param name="threshold">The maximum duration a job should remain in processing state.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A read-only list of stuck processing review jobs.</returns>
    Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default);

    /// <summary>Returns the number of review jobs currently in the Processing state.</summary>
    Task<int> CountProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Attempts to transition a review job from one status to another.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="from">The current job status.</param>
    /// <param name="to">The target job status.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>true</c> if the transition was successful; otherwise <c>false</c>.</returns>
    Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default);

    /// <summary>
    ///     Updates the retry count of a review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="retryCount">The new retry count.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default);

    /// <summary>
    ///     Marks a review job as failed with an error message.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="errorMessage">The error message describing the failure.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default);

    /// <summary>
    ///     Deletes a review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Sets the result of a review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="result">The review result.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default);

    /// <summary>
    ///     Adds a file result to a review job.
    /// </summary>
    /// <param name="result">The review file result to add.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    /// <summary>
    ///     Gets a completed review job with all its file results.
    /// </summary>
    /// <param name="organizationUrl">The organization URL.</param>
    /// <param name="projectId">The project identifier.</param>
    /// <param name="repositoryId">The repository identifier.</param>
    /// <param name="pullRequestId">The pull request identifier.</param>
    /// <param name="iterationId">The iteration identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The completed review job with file results if found; otherwise <c>null</c>.</returns>
    Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks a review job as cancelled.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCancelledAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Updates the AI configuration of a review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="connectionId">The AI connection identifier, or <c>null</c> to clear it.</param>
    /// <param name="model">The AI model identifier, or <c>null</c> to clear it.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default, float? reviewTemperature = null);
}
