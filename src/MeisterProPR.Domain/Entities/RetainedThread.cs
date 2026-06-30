// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     A retained pull-request review thread under a <see cref="RetainedPullRequest" />. Structured
///     thread metadata is kept in plaintext for querying; only the comment bodies (held by the
///     child <see cref="RetainedThreadComment" /> rows) are encrypted at rest.
/// </summary>
public sealed class RetainedThread
{
    /// <summary>Unique identifier for this retained thread.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning retained pull request.</summary>
    public Guid RetainedPullRequestId { get; init; }

    /// <summary>Provider thread identifier. Unique per retained pull request.</summary>
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>File path the thread is anchored to. Null for pull-request-level threads.</summary>
    public string? FilePath { get; set; }

    /// <summary>Line the thread is anchored to. Null for pull-request-level threads.</summary>
    public int? Line { get; set; }

    /// <summary>Last-known thread status, e.g. "active", "resolved", "closed".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this thread snapshot was last upserted.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Navigation back to the owning retained pull request.</summary>
    public RetainedPullRequest? RetainedPullRequest { get; init; }

    /// <summary>Retained comments belonging to this thread.</summary>
    public ICollection<RetainedThreadComment> Comments { get; init; } = new List<RetainedThreadComment>();
}
