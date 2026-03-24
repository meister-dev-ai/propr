using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Interface for managing review jobs in the repository.</summary>
public interface IJobRepository
{
    /// <summary>Persists a new review job.</summary>
    Task AddAsync(ReviewJob job, CancellationToken ct = default);

    /// <summary>Returns the first non-Failed job for the given PR iteration, or null.</summary>
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

    /// <summary>All jobs for a client, newest first.</summary>
    /// <param name="clientId">The client identifier to filter jobs by.</param>
    IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId);

    /// <summary>Returns all jobs across all clients, newest first, with optional status filter and pagination.</summary>
    Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        CancellationToken ct = default);

    /// <summary>Gets a job by id, or null if not found.</summary>
    ReviewJob? GetById(Guid id);

    /// <summary>Returns all jobs with Status == Pending, oldest first.</summary>
    IReadOnlyList<ReviewJob> GetPendingJobs();

    /// <summary>Returns all jobs currently in the Processing state.</summary>
    Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default);

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

    /// <summary>Persists agentic review loop metadata after a review completes.</summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="toolCallCount">Total number of tool calls made during the loop.</param>
    /// <param name="toolCalls">JSON-serialised array of tool call records, or <see langword="null" />.</param>
    /// <param name="confidenceEvaluations">JSON-serialised confidence evaluation array, or <see langword="null" />.</param>
    /// <param name="finalConfidence">Final aggregated confidence score, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetAgenticMetadataAsync(
        Guid id,
        int toolCallCount,
        string? toolCalls,
        string? confidenceEvaluations,
        int? finalConfidence,
        CancellationToken ct = default);
}
