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
    ///     Posts a reply into the target review thread and reports the provider-native identifier of the
    ///     comment it created, or null when the adapter cannot obtain one.
    /// </summary>
    /// <remarks>
    ///     The comment id alone, rather than a result also carrying the thread id: a reply lands in the thread
    ///     the caller named, so every other coordinate provenance recording needs is already in the caller's
    ///     <see cref="ReviewThreadRef" />, and echoing it back would only invite the two to disagree. Nullable
    ///     because an adapter that genuinely cannot report an id must still be free to post: the reply degrades
    ///     to posted-but-unrecorded instead of being blocked.
    /// </remarks>
    Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default);
}
