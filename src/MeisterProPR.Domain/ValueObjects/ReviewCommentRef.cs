// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Stable identity for a provider comment inside a review thread.</summary>
public sealed record ReviewCommentRef
{
    /// <summary>Initializes a new instance of the <see cref="ReviewCommentRef" /> class.</summary>
    /// <param name="thread">The review thread containing this comment.</param>
    /// <param name="externalCommentId">The external identifier for the comment.</param>
    /// <param name="author">The identity of the comment author.</param>
    /// <param name="publishedAt">The date and time when the comment was published.</param>
    public ReviewCommentRef(
        ReviewThreadRef thread,
        string externalCommentId,
        ReviewerIdentity author,
        DateTimeOffset? publishedAt)
    {
        this.Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        this.Author = author ?? throw new ArgumentNullException(nameof(author));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalCommentId);

        this.ExternalCommentId = externalCommentId.Trim();
        this.PublishedAt = publishedAt;
    }

    /// <summary>Gets the review thread containing this comment.</summary>
    public ReviewThreadRef Thread { get; }

    /// <summary>Gets the external identifier for the comment.</summary>
    public string ExternalCommentId { get; }

    /// <summary>Gets the identity of the comment author.</summary>
    public ReviewerIdentity Author { get; }

    /// <summary>Gets the date and time when the comment was published.</summary>
    public DateTimeOffset? PublishedAt { get; }
}
