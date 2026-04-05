// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents an existing comment thread on a pull request, from any author or iteration.
/// </summary>
/// <param name="ThreadId">ADO thread identifier.</param>
/// <param name="FilePath">File path the thread is anchored to, or <c>null</c> for PR-level threads.</param>
/// <param name="LineNumber">Line number the thread is anchored to, or <c>null</c> for file- or PR-level threads.</param>
/// <param name="Comments">Comments within this thread, ordered chronologically.</param>
/// <param name="Status">
///     ADO thread status string (e.g. "Active", "Fixed", "Closed", "WontFix", "ByDesign").
///     <c>null</c> when not provided or unknown.
/// </param>
public sealed record PrCommentThread(
    int ThreadId,
    string? FilePath,
    int? LineNumber,
    IReadOnlyList<PrThreadComment> Comments,
    string? Status = null);

/// <summary>
///     Represents a single comment within a <see cref="PrCommentThread" />.
/// </summary>
/// <param name="AuthorName">Display name of the comment author from ADO.</param>
/// <param name="Content">Raw text content of the comment.</param>
/// <param name="AuthorId">
///     VSS identity GUID of the comment author, as returned by the ADO comments API.
///     <c>null</c> when the author ID could not be parsed or was not provided.
/// </param>
/// <param name="CommentId">
///     ADO comment ID within the thread. Used for deduplication in mention scanning.
///     Defaults to <c>0</c> when not provided (e.g. older call sites).
/// </param>
/// <param name="PublishedAt">
///     When the comment was published in ADO. Used as the per-PR watermark in mention scanning.
///     <c>null</c> when not provided.
/// </param>
public sealed record PrThreadComment(
    string AuthorName,
    string Content,
    Guid? AuthorId = null,
    int CommentId = 0,
    DateTimeOffset? PublishedAt = null);
