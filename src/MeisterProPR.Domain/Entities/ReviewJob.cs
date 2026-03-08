using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Represents a request to run a review job for a pull request.
/// </summary>
public sealed class ReviewJob
{
    /// <summary>
    ///     Creates a new <see cref="ReviewJob" />.
    /// </summary>
    public ReviewJob(
        Guid id,
        string clientKey,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(clientKey))
        {
            throw new ArgumentException("ClientKey required.", nameof(clientKey));
        }

        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            throw new ArgumentException("OrganizationUrl required.", nameof(organizationUrl));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("ProjectId required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("RepositoryId required.", nameof(repositoryId));
        }

        if (pullRequestId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestId));
        }

        if (iterationId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterationId));
        }

        this.Id = id;
        this.ClientKey = clientKey;
        this.OrganizationUrl = organizationUrl;
        this.ProjectId = projectId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.IterationId = iterationId;
        this.Status = JobStatus.Pending;
        this.SubmittedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Unique identifier for the review job.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    ///     Current status of the job.
    /// </summary>
    public JobStatus Status { get; set; }

    /// <summary>
    ///     Client key that submitted the job.
    /// </summary>
    public string ClientKey { get; init; }

    /// <summary>
    ///     Organization URL containing the repository.
    /// </summary>
    public string OrganizationUrl { get; init; }

    /// <summary>
    ///     Project identifier in the organization.
    /// </summary>
    public string ProjectId { get; init; }

    /// <summary>
    ///     Repository identifier.
    /// </summary>
    public string RepositoryId { get; init; }

    /// <summary>
    ///     Pull request identifier.
    /// </summary>
    public int PullRequestId { get; init; }

    /// <summary>
    ///     Iteration identifier within the pull request.
    /// </summary>
    public int IterationId { get; init; }

    /// <summary>
    ///     When the job was submitted.
    /// </summary>
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    ///     When the job completed, if available.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    ///     Result of the review, if completed.
    /// </summary>
    public ReviewResult? Result { get; set; }

    /// <summary>
    ///     Error message if the job failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}