// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Aggregate root for the code-insight facts collected about a single pull request. One row per
///     (client, repository, pull request); it is the per-pull-request purge unit, so deleting this row
///     cascades to every finding collected under it.
///     The store is intentionally independent of the review, memory, and review-archive tables: the link
///     back to them is by (pull request id + provider thread id) values only, never a foreign key.
/// </summary>
public sealed class CodeInsightPullRequest
{
    /// <summary>Unique identifier for this aggregate.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client: scopes the collected data and cascades its deletion.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider repository identifier the pull request belongs to.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Provider pull-request identifier.</summary>
    public long PullRequestId { get; init; }

    /// <summary>
    ///     The repository's display name as the provider reports it, or <see langword="null" /> when no review has
    ///     told us one yet. Display only: identity, filtering, and every join stay on <see cref="RepositoryId" />,
    ///     which is the provider's own identifier and for several providers is a bare number.
    /// </summary>
    /// <remarks>
    ///     Refreshed whenever a review touches the aggregate, so a renamed repository catches up on its next review
    ///     rather than being stuck with the name it had when it was first collected.
    /// </remarks>
    public string? RepositoryName { get; set; }

    /// <summary>Last-known pull-request lifecycle state, e.g. "Active", "Completed", "Abandoned".</summary>
    public string PullRequestState { get; set; } = string.Empty;

    /// <summary>
    ///     Stored revision key of the newest increment collected for this pull request. What makes a finding
    ///     chain's fate readable without re-deriving it: a chain whose newest row carries this key was still
    ///     being raised, and one whose newest row carries an older key stopped being reported.
    /// </summary>
    public string LatestRevisionKey { get; set; } = string.Empty;

    /// <summary>
    ///     UTC timestamp of the most recent collection activity for this pull request. This is the
    ///     retention anchor: the purge sweep removes aggregates whose activity is older than the cutoff.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>UTC timestamp when this aggregate was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when this aggregate was last upserted.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Findings collected under this pull request.</summary>
    public ICollection<CodeInsightFinding> Findings { get; init; } = new List<CodeInsightFinding>();
}
