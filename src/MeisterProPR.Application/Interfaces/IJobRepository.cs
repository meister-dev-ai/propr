using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Interface for managing review jobs in the repository.
/// </summary>
public interface IJobRepository
{
    /// <summary>Adds a new review job to the repository.</summary>
    void Add(ReviewJob job);

    /// <summary>Returns the first non-Failed job for the given PR iteration, or null.</summary>
    /// <param name="organizationUrl">Base URL of the Azure DevOps organization (e.g., https://dev.azure.com/yourorg).</param>
    /// <param name="projectId">ID of the Azure DevOps project.</param>
    /// <param name="repositoryId">ID of the repository containing the pull request.</param>
    /// <param name="pullRequestId">Numeric ID of the pull request.</param>
    /// <param name="iterationId">ID of the pull request iteration.</param>
    /// <returns>A review job if found; otherwise, null.</returns>
    ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>All jobs for a client, newest first.</summary>
    /// <param name="clientId">The client identifier to filter jobs by.</param>
    /// <returns>List of all review jobs for the specified client.</returns>
    IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId);

    /// <summary>Returns all jobs across all clients, newest first, with optional status filter and pagination.</summary>
    Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        CancellationToken ct = default);

    /// <summary>Gets a job by id, or null if not found.</summary>
    /// <param name="id">The unique identifier of the review job.</param>
    /// <returns>The review job if found; otherwise, null.</returns>
    ReviewJob? GetById(Guid id);

    /// <summary>Returns all jobs with Status == Pending, oldest first.</summary>
    /// <returns>List of pending review jobs.</returns>
    IReadOnlyList<ReviewJob> GetPendingJobs();

    /// <summary>Returns all jobs currently in the Processing state.</summary>
    Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>Marks job as failed with an error message.</summary>
    /// <param name="id">The unique identifier of the review job.</param>
    /// <param name="errorMessage">A message describing the reason for the failure.</param>
    void SetFailed(Guid id, string errorMessage);

    /// <summary>Sets the review result for a completed job.</summary>
    /// <param name="id">The unique identifier of the review job.</param>
    /// <param name="result">The result of the review to be associated with the job.</param>
    void SetResult(Guid id, ReviewResult result);

    /// <summary>Atomic compare-and-swap on Status. Returns false if current status != from.</summary>
    /// <param name="id">The unique identifier of the review job.</param>
    /// <param name="from">The current status to compare against.</param>
    /// <param name="to">The new status to set if the current status matches.</param>
    /// <returns>True if the status was successfully updated; otherwise, false.</returns>
    bool TryTransition(Guid id, JobStatus from, JobStatus to);
}
