// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Lightweight projection of a single PR reviewer thread used by
///     <see cref="MeisterDev.ProPR.Application.Interfaces.IReviewerThreadStatusFetcher" />.
/// </summary>
/// <param name="ThreadId">
///     The identifier the provider itself uses for the thread, carried verbatim. <c>null</c> where the
///     provider has no thread object of its own and the threads were grouped client-side.
/// </param>
/// <param name="Status">Opaque current provider thread status.</param>
/// <param name="FilePath">File path the thread is anchored to; null for PR-level threads.</param>
/// <param name="CommentHistory">
///     All normalized non-system comments concatenated chronologically as
///     <c>{author}: {content}</c> lines and truncated to a configurable maximum length.
/// </param>
/// <param name="NonReviewerReplyCount">
///     Count of non-system, non-deleted comments in the thread whose author is not the reviewer.
///     Used by the crawl service to detect same-iteration conversational follow-up without
///     creating a duplicate review job for unchanged pull requests.
/// </param>
/// <param name="CodeChangedSinceRaised">
///     Whether the code the thread is anchored to has changed since the finding was first raised, as
///     determined by the provider (e.g. an outdated diff hunk). Determines whether a claimed-fix closure is
///     trusted as memory. Defaults to <see cref="ThreadAnchorCodeChange.Unknown" /> when a provider
///     does not supply the signal.
/// </param>
public sealed record PrThreadStatusEntry(
    string? ThreadId,
    string Status,
    string? FilePath,
    string CommentHistory,
    int NonReviewerReplyCount = 0,
    ThreadAnchorCodeChange CodeChangedSinceRaised = ThreadAnchorCodeChange.Unknown);
