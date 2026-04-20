// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Lightweight projection of a single PR reviewer thread used by
///     <see cref="MeisterProPR.Application.Interfaces.IReviewerThreadStatusFetcher" />.
/// </summary>
/// <param name="ThreadId">ADO thread identifier.</param>
/// <param name="Status">Current ADO thread status string (e.g. <c>Active</c>, <c>Fixed</c>).</param>
/// <param name="FilePath">File path the thread is anchored to; null for PR-level threads.</param>
/// <param name="CommentHistory">
///     All non-system comments (<c>commentType != "system"</c>) concatenated chronologically as
///     <c>{author}: {content}</c> lines and truncated to a configurable maximum length.
/// </param>
/// <param name="NonReviewerReplyCount">
///     Count of non-system, non-deleted comments in the thread whose author is not the reviewer.
///     Used by the crawl service to detect same-iteration conversational follow-up without
///     creating a duplicate review job for unchanged pull requests.
/// </param>
public sealed record PrThreadStatusEntry(
    int ThreadId,
    string Status,
    string? FilePath,
    string CommentHistory,
    int NonReviewerReplyCount = 0);
