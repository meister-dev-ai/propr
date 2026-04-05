// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Tracks the last processed commit (ADO iteration ID stored as a string) for a pull request
///     per client, enabling the system to skip re-evaluation when no new commits have been pushed
///     and to detect when thread replies require a conversational response (FR-001, FR-005).
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
    ///     The identifier of the last commit (ADO iteration ID) processed for comment resolution.
    ///     Must not be null or empty.
    /// </param>
    public ReviewPrScan(
        Guid id,
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string lastProcessedCommitId)
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

        if (string.IsNullOrEmpty(lastProcessedCommitId))
        {
            throw new ArgumentException("LastProcessedCommitId must not be null or empty.", nameof(lastProcessedCommitId));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.LastProcessedCommitId = lastProcessedCommitId;
        this.UpdatedAt = DateTimeOffset.UtcNow;
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
    ///     The identifier of the last commit processed for comment resolution.
    ///     Stored as the ADO iteration ID string (e.g. "3"). If the current PR iteration
    ///     differs from this value, new commits have been pushed and comment resolution
    ///     should be re-evaluated.
    /// </summary>
    public string LastProcessedCommitId { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    ///     Per-thread reply watermarks. Used to detect new human replies in reviewer threads
    ///     even when no new commits have been pushed (FR-005 exception path).
    /// </summary>
    public ICollection<ReviewPrScanThread> Threads { get; init; } = new List<ReviewPrScanThread>();
}
