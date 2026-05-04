// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Interface for managing review jobs in the repository.</summary>
public interface IJobRepository
{
    /// <summary>Persists a new review job.</summary>
    Task AddAsync(ReviewJob job, CancellationToken ct = default);

    /// <summary>Returns the first Pending or Processing job for the given PR iteration, or null.</summary>
    /// <param name="organizationUrl">Base URL of the Azure DevOps organization.</param>
    /// <param name="projectId">ID of the Azure DevOps project.</param>
    /// <param name="repositoryId">ID of the repository containing the pull request.</param>
    /// <param name="pullRequestId">Numeric ID of the pull request.</param>
    /// <param name="iterationId">ID of the pull request iteration.</param>
    ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>Returns the most-recent Completed job for the given PR iteration, or null.</summary>
    ReviewJob? FindCompletedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>All jobs for a client, newest first.</summary>
    /// <param name="clientId">The client identifier to filter jobs by.</param>
    IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId);

    /// <summary>Returns all jobs across all clients, newest first, with optional status filter and pagination.</summary>
    Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default);

    /// <summary>Gets a job by id, or null if not found.</summary>
    ReviewJob? GetById(Guid id);

    /// <summary>Returns all jobs with Status == Pending, oldest first.</summary>
    IReadOnlyList<ReviewJob> GetPendingJobs();

    /// <summary>Returns all jobs currently in the Processing state.</summary>
    Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>Returns the number of jobs currently in the Processing state.</summary>
    Task<int> CountProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>Returns jobs that have been in <c>Processing</c> state for longer than the given threshold.</summary>
    Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default);

    /// <summary>Updates the retry count for a review job.</summary>
    Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default);

    /// <summary>Marks the job as failed with an error message.</summary>
    Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default);

    /// <summary>Deletes a job by id. No-op if the job does not exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Sets the review result for a completed job.</summary>
    Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default);

    /// <summary>
    ///     Atomic compare-and-swap on Status.
    ///     Returns <c>false</c> if the current status does not match <paramref name="from" />.
    /// </summary>
    Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>FileReviewResults</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    /// </summary>
    Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a per-file review result for a job.</summary>
    Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    /// <summary>Updates a per-file review result for a job.</summary>
    Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>Protocols</c> and <c>Protocols.Events</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    /// </summary>
    Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>Protocol</c> and <c>Protocol.Events</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    ///     This is the only sanctioned path for reading protocol data (ReviewJob is the aggregate root).
    /// </summary>
    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks the job as cancelled. No-op if the job does not exist or is already in a terminal state.</summary>
    Task SetCancelledAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns all Pending or Processing jobs for the given ADO organisation/project combination.</summary>
    Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
        string organizationUrl,
        string projectId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns jobs for a specific pull request, newest first, with pagination.
    ///     Includes <c>Protocols</c> and <c>Protocols.Events</c> eagerly loaded.
    /// </summary>
    Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the most-recent Completed job for the given PR iteration with
    ///     <see cref="ReviewJob.FileReviewResults" /> eagerly loaded, or <see langword="null" />.
    /// </summary>
    Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default);

    /// <summary>Persists the AI connection snapshot captured at job-start time.</summary>
    Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default, float? reviewTemperature = null);

    /// <summary>Persists the PR context snapshot captured after job creation.</summary>
    Task UpdatePrContextAsync(
        Guid id,
        string? prTitle,
        string? prRepositoryName,
        string? prSourceBranch,
        string? prTargetBranch,
        CancellationToken ct = default);
}
