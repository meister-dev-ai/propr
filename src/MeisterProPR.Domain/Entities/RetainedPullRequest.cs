// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Aggregate root for opt-in retained raw pull-request data. One row represents a single
///     pull request whose threads and file diffs are retained for later inspection. This is the
///     per-PR purge unit: deleting this row cascades to its retained threads, comments, and diffs.
///     The store is intentionally independent of review/memory tables — the natural link back to
///     memory is by (pull request id + thread id) values only, never a foreign key.
/// </summary>
public sealed class RetainedPullRequest
{
    /// <summary>Unique identifier for this retained pull request.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client — scopes the retained data.</summary>
    public Guid ClientId { get; init; }

    /// <summary>SCM provider connection the pull request belongs to.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Provider repository identifier the pull request belongs to.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Provider pull-request identifier.</summary>
    public long PullRequestId { get; init; }

    /// <summary>Last-known pull-request lifecycle state, e.g. "open", "closed", "merged".</summary>
    public string PrState { get; set; } = string.Empty;

    /// <summary>
    ///     UTC timestamp of the most recent activity observed for this pull request. This is the
    ///     retention anchor: the purge worker removes pull requests whose activity is older than the cutoff.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>UTC timestamp when this retained pull request was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when this retained pull request was last upserted.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Retained threads belonging to this pull request.</summary>
    public ICollection<RetainedThread> Threads { get; init; } = new List<RetainedThread>();

    /// <summary>Retained per-file diffs belonging to this pull request.</summary>
    public ICollection<RetainedFileDiff> FileDiffs { get; init; } = new List<RetainedFileDiff>();
}
