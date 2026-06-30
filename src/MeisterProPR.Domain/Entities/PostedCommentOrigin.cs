// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Provenance row mapping a single provider-native review comment back to the review job that
///     posted it. A later ingestion step uses this mapping to stamp the originating job onto retained
///     comments. Rows share the lifecycle of the retained pull-request data: the retention purge removes
///     them alongside the PRs they belong to. The store is independent of review/memory tables — the
///     only link is by (client + repository + pull request + provider comment) values, never a foreign key.
/// </summary>
public sealed class PostedCommentOrigin
{
    /// <summary>Unique identifier for this provenance row.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client — scopes the provenance data.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider repository identifier the pull request belongs to.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Provider pull-request identifier.</summary>
    public long PullRequestId { get; init; }

    /// <summary>Provider thread identifier the comment belongs to, when the provider exposes one.</summary>
    public string? ProviderThreadId { get; set; }

    /// <summary>Provider-native comment identifier this provenance row maps from.</summary>
    public string ProviderCommentId { get; init; } = string.Empty;

    /// <summary>Review job that posted the comment.</summary>
    public Guid JobId { get; set; }

    /// <summary>UTC timestamp when the comment was posted.</summary>
    public DateTimeOffset PostedAt { get; set; }
}
