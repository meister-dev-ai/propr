// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     AI core for evaluating whether a reviewer-owned pull-request comment thread has been resolved.
///     Provides two prompt paths: code-change evaluation and conversational reply.
/// </summary>
public interface IAiCommentResolutionCore
{
    /// <summary>
    ///     Evaluates whether a code change addresses the issue raised in <paramref name="thread" />.
    ///     Called when a new PR iteration (commit) has been detected since the thread was last processed.
    /// </summary>
    /// <param name="thread">The reviewer-owned comment thread to evaluate.</param>
    /// <param name="pr">The pull request containing the latest diff and full file contents.</param>
    /// <param name="chatClient">The client-scoped AI chat client to use.</param>
    /// <param name="modelId">The model deployment identifier for the client-scoped AI connection.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="outputLanguage">
    ///     The client's configured output language as an IETF BCP 47 tag, stated in the system prompt so the reply
    ///     is written in the same language as the rest of the review. <see langword="null" /> states no language.
    /// </param>
    /// <returns>
    ///     A <see cref="ThreadResolutionResult" /> indicating whether the issue is resolved and
    ///     an optional reply to post in the thread.
    /// </returns>
    Task<ThreadResolutionResult> EvaluateCodeChangeAsync(
        PrCommentThread thread,
        PullRequest pr,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default,
        string? outputLanguage = null);

    /// <summary>
    ///     Generates a conversational response to new human replies in <paramref name="thread" />,
    ///     when no new commits have been pushed since the thread was last processed.
    /// </summary>
    /// <param name="thread">The reviewer-owned comment thread containing the new replies.</param>
    /// <param name="chatClient">The client-scoped AI chat client to use.</param>
    /// <param name="modelId">The model deployment identifier for the client-scoped AI connection.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="outputLanguage">
    ///     The client's configured output language as an IETF BCP 47 tag, stated in the system prompt so the reply
    ///     is written in the same language as the rest of the review. <see langword="null" /> states no language.
    /// </param>
    /// <returns>
    ///     A <see cref="ThreadResolutionResult" /> with <c>IsResolved = false</c> and
    ///     a <c>ReplyText</c> to post as a conversational follow-up.
    /// </returns>
    Task<ThreadResolutionResult> EvaluateConversationalReplyAsync(
        PrCommentThread thread,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default,
        string? outputLanguage = null);
}
