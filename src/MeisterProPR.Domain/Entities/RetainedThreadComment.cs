// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     A retained comment under a <see cref="RetainedThread" />. The comment body is the only
///     sensitive field and is stored encrypted at rest in <see cref="EncryptedText" />; all other
///     fields (author identity, is-AI flag, timestamps, identifiers) stay plaintext for querying.
/// </summary>
public sealed class RetainedThreadComment
{
    /// <summary>Unique identifier for this retained comment.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning retained thread.</summary>
    public Guid RetainedThreadId { get; init; }

    /// <summary>Provider comment identifier.</summary>
    public string CommentId { get; init; } = string.Empty;

    /// <summary>Provider-neutral author identity for the comment.</summary>
    public string AuthorIdentity { get; init; } = string.Empty;

    /// <summary>Whether the comment was authored by the AI reviewer.</summary>
    public bool IsAiAuthored { get; init; }

    /// <summary>UTC timestamp when the comment was published on the provider.</summary>
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>The comment body, stored encrypted at rest.</summary>
    public string EncryptedText { get; set; } = string.Empty;

    /// <summary>
    ///     The review job that produced this comment, when the comment was posted by the AI reviewer and
    ///     its provenance is retained; null for comments with no recorded originating job (human comments,
    ///     or AI comments whose provenance has been purged or was never recorded).
    /// </summary>
    public Guid? OriginatingJobId { get; set; }

    /// <summary>Navigation back to the owning retained thread.</summary>
    public RetainedThread? RetainedThread { get; init; }
}
