// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Publishes a reply into a provider-native review thread.</summary>
public interface IReviewThreadReplyPublisher
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>
    ///     Whether this adapter needs <see cref="ReviewThreadRef.ExternalThreadId" /> to post a reply.
    /// </summary>
    /// <remarks>
    ///     True for every adapter that replies inside the thread, because the identifier is what it addresses.
    ///     False for one that answers with a new comment on the pull request: it addresses the pull request and
    ///     says which comment it answers with a quote, so a thread it was never given is no obstacle. Callers
    ///     read this before refusing a comment whose thread has no identifier.
    /// </remarks>
    bool RequiresThreadIdentifier => true;

    /// <summary>
    ///     Posts a reply into the target review thread and reports the provider-native identifier of the
    ///     comment it created, or null when the adapter cannot obtain one.
    /// </summary>
    /// <remarks>
    ///     The result contains the comment identifier. A reply is posted in the thread
    ///     the caller named, so every other coordinate provenance recording needs is already in the caller's
    ///     <see cref="ReviewThreadRef" />, and echoing it back would only invite the two to disagree. Nullable
    ///     because an adapter that cannot report an id must still be free to post: the reply degrades
    ///     to posted-but-unrecorded instead of being blocked.
    /// </remarks>
    /// <param name="clientId">The client whose credentials the reply is posted with.</param>
    /// <param name="thread">The thread being replied to.</param>
    /// <param name="replyText">What to say.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <param name="quotedComment">
    ///     The text of the comment being answered. Adapters that post a new pull-request comment use a
    ///     Markdown blockquote to identify the question. Adapters that reply inside a thread can ignore it.
    /// </param>
    Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default,
        string? quotedComment = null);
}
