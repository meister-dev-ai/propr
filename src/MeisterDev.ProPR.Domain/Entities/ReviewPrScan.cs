// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Tracks the last processed provider revision key for a pull request per client, enabling the
///     system to skip re-evaluation when no new commits have been pushed and to detect when thread
///     replies require a conversational response.
///     One row per (ClientId, OrganizationUrl, ProjectId, RepositoryId, PullRequestId).
/// </summary>
/// <remarks>
///     The host and project are part of the identity because a repository identifier is only unique within
///     the host that issued it. Two providers hand out small integers freely, so one client holding a GitLab
///     project 4 and a Forgejo repository 4 would otherwise share a row for every pull request number they
///     have in common — and read each other's watermarks. That is the same identity
///     <c>review_jobs</c> is keyed on, and the reason the engaged-revision lookup beside it was already safe.
/// </remarks>
public sealed class ReviewPrScan
{
    /// <summary>
    ///     Creates a new <see cref="ReviewPrScan" />.
    /// </summary>
    /// <param name="id">Unique identifier — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="clientId">Owning client identifier — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="organizationUrl">The host the repository identifier was issued by.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">ADO repository identifier — must not be null or whitespace.</param>
    /// <param name="pullRequestId">ADO pull request number — must be greater than zero.</param>
    /// <param name="lastProcessedCommitId">
    ///     The identifier of the last processed revision key. Azure DevOps stores the iteration ID
    ///     string (for example, "3"), while provider-neutral flows may persist a non-numeric
    ///     provider revision identifier or patch identity.
    /// </param>
    public ReviewPrScan(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string lastProcessedCommitId)
        : this(id, clientId, organizationUrl, projectId, repositoryId, pullRequestId)
    {
        if (string.IsNullOrEmpty(lastProcessedCommitId))
        {
            throw new ArgumentException(
                "LastProcessedCommitId must not be null or empty.",
                nameof(lastProcessedCommitId));
        }

        this.LastProcessedCommitId = lastProcessedCommitId;
    }

    private ReviewPrScan(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            throw new ArgumentException("OrganizationUrl must not be null or whitespace.", nameof(organizationUrl));
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("RepositoryId must not be null or whitespace.", nameof(repositoryId));
        }

        if (pullRequestId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestId), "PullRequestId must be greater than zero.");
        }

        this.Id = id;
        this.ClientId = clientId;
        this.OrganizationUrl = organizationUrl;

        // Empty is allowed where the host needs no project to address a repository, which is every provider
        // but Azure DevOps. It still takes part in the identity, so it is stored rather than dropped.
        this.ProjectId = projectId ?? string.Empty;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.LastProcessedCommitId = string.Empty;
        this.LastThreadPassRevisionKey = string.Empty;
        this.PendingReviewRevisionKey = string.Empty;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Creates the scan record from the thread pass, which reaches a pull request the file pass has not
    ///     recorded a revision for. Only the thread watermark is seeded; the review watermark stays empty
    ///     until the file pass writes its own, because a pass that reviewed no code must not leave the pull
    ///     request recorded as reviewed.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="organizationUrl">The host the repository identifier was issued by.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="threadPassRevisionKey">The revision key the thread pass has now checked the threads at.</param>
    /// <returns>The new record.</returns>
    public static ReviewPrScan ForThreadPass(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string threadPassRevisionKey)
    {
        if (string.IsNullOrEmpty(threadPassRevisionKey))
        {
            throw new ArgumentException(
                "ThreadPassRevisionKey must not be null or empty.",
                nameof(threadPassRevisionKey));
        }

        return new ReviewPrScan(id, clientId, organizationUrl, projectId, repositoryId, pullRequestId)
        {
            LastThreadPassRevisionKey = threadPassRevisionKey,
        };
    }

    /// <summary>
    ///     Creates the scan record for a pull request that has moved past the revision it was reviewed at and
    ///     was left unreviewed. The review watermark stays empty for the same reason it does above: declining
    ///     to review a revision is not a record of having reviewed one.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="organizationUrl">The host the repository identifier was issued by.</param>
    /// <param name="projectId">The project within that host, empty where the host has none.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="pendingReviewRevisionKey">The revision the pull request now sits at, unreviewed.</param>
    /// <param name="detectedAt">When that revision was first seen and declined.</param>
    /// <returns>The new record.</returns>
    public static ReviewPrScan ForPendingReview(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string pendingReviewRevisionKey,
        DateTimeOffset detectedAt)
    {
        if (string.IsNullOrEmpty(pendingReviewRevisionKey))
        {
            throw new ArgumentException(
                "PendingReviewRevisionKey must not be null or empty.",
                nameof(pendingReviewRevisionKey));
        }

        return new ReviewPrScan(id, clientId, organizationUrl, projectId, repositoryId, pullRequestId)
        {
            PendingReviewRevisionKey = pendingReviewRevisionKey,
            PendingReviewDetectedAt = detectedAt,
        };
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the client that owns this scan record.</summary>
    public Guid ClientId { get; init; }

    /// <summary>The host that issued <see cref="RepositoryId" />, and so the scope it is unique within.</summary>
    public string OrganizationUrl { get; init; }

    /// <summary>The project within the host, empty where the host addresses repositories without one.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Provider repository identifier, unique only within <see cref="OrganizationUrl" />.</summary>
    public string RepositoryId { get; init; }

    /// <summary>ADO pull request number.</summary>
    public int PullRequestId { get; init; }

    /// <summary>
    ///     The identifier of the last revision the files were reviewed at, written by the file pass alone.
    ///     Azure DevOps stores the iteration ID string (for example, "3"); provider-neutral flows
    ///     may persist a non-numeric provider revision identifier or patch identity. Empty when the record
    ///     was brought into being by the thread pass and no file review has recorded a revision yet.
    /// </summary>
    public string LastProcessedCommitId { get; set; }

    /// <summary>
    ///     The identifier of the last revision the reviewer's comment threads were checked at, written by the
    ///     thread pass alone. Empty when no thread pass has completed, which differs from every revision and
    ///     so makes the next pass due.
    /// </summary>
    public string LastThreadPassRevisionKey { get; set; } = string.Empty;

    /// <summary>
    ///     The revision an automatic trigger saw and declined to review, because the client reviews only a
    ///     pull request's first increment. Empty when nothing was declined.
    /// </summary>
    /// <remarks>
    ///     This is the durable answer to "has this pull request moved on since it was reviewed?", which
    ///     nothing else on the record can answer: the head revision is known only to whoever just spoke to the
    ///     provider, and a read surface has not. Whether the pull request is actually ahead is derived by
    ///     comparing this against <see cref="LastProcessedCommitId" /> rather than stored, so a review of this
    ///     very revision retires the state by writing its own watermark and no second write can be missed.
    /// </remarks>
    public string PendingReviewRevisionKey { get; set; } = string.Empty;

    /// <summary>
    ///     When <see cref="PendingReviewRevisionKey" /> was first seen and declined, so a reader can say how
    ///     long the pull request has been waiting. Null when nothing was declined. Only moves when the pending
    ///     revision itself changes, so a crawl that re-declines the same revision does not reset the clock.
    /// </summary>
    public DateTimeOffset? PendingReviewDetectedAt { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    ///     Per-thread reply watermarks. Used to detect new human replies in reviewer threads
    ///     even when no new commits have been pushed.
    /// </summary>
    public ICollection<ReviewPrScanThread> Threads { get; init; } = new List<ReviewPrScanThread>();
}
