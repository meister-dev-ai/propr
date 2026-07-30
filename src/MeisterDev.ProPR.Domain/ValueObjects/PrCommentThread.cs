// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

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
    long ThreadId,
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
/// <param name="IsSystemGenerated">
///     <c>true</c> when the provider wrote this entry itself to record an activity rather than a person writing
///     it: "added a reviewer", "updated the source branch", a vote, a policy result. Providers return these
///     through the same comments API as human replies, so anything that reasons about what a human said has to
///     exclude them. <c>false</c> when the provider gave no signal either way, which is the safe default: an
///     unmarked activity entry is a misread comment, while a human comment marked as activity is a lost one.
/// </param>
public sealed record PrThreadComment(
    string AuthorName,
    string Content,
    Guid? AuthorId = null,
    long CommentId = 0,
    DateTimeOffset? PublishedAt = null,
    bool IsSystemGenerated = false);
