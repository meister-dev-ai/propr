// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Tracks the last processed provider revision key for a pull request per client, enabling the
///     system to skip re-evaluation when no new commits have been pushed and to detect when thread
///     replies require a conversational response.
///     One row per (ClientId, RepositoryId, PullRequestId) triple.
/// </summary>
public sealed class ReviewPrScan
{
    /// <summary>
    ///     Creates a new <see cref="ReviewPrScan" />.
    /// </summary>
    /// <param name="id">Unique identifier — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="clientId">Owning client identifier — must not be <see cref="Guid.Empty" />.</param>
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
        string repositoryId,
        int pullRequestId,
        string lastProcessedCommitId)
        : this(id, clientId, repositoryId, pullRequestId)
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
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="threadPassRevisionKey">The revision key the thread pass has now checked the threads at.</param>
    /// <returns>The new record.</returns>
    public static ReviewPrScan ForThreadPass(
        Guid id,
        Guid clientId,
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

        return new ReviewPrScan(id, clientId, repositoryId, pullRequestId)
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
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="pendingReviewRevisionKey">The revision the pull request now sits at, unreviewed.</param>
    /// <param name="detectedAt">When that revision was first seen and declined.</param>
    /// <returns>The new record.</returns>
    public static ReviewPrScan ForPendingReview(
        Guid id,
        Guid clientId,
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

        return new ReviewPrScan(id, clientId, repositoryId, pullRequestId)
        {
            PendingReviewRevisionKey = pendingReviewRevisionKey,
            PendingReviewDetectedAt = detectedAt,
        };
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the client that owns this scan record.</summary>
    public Guid ClientId { get; init; }

    /// <summary>ADO repository identifier.</summary>
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
