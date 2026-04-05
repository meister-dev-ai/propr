// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

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
    /// <returns>
    ///     A <see cref="ThreadResolutionResult" /> indicating whether the issue is resolved and
    ///     an optional reply to post in the thread.
    /// </returns>
    Task<ThreadResolutionResult> EvaluateCodeChangeAsync(
        PrCommentThread thread,
        PullRequest pr,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Generates a conversational response to new human replies in <paramref name="thread" />,
    ///     when no new commits have been pushed since the thread was last processed.
    /// </summary>
    /// <param name="thread">The reviewer-owned comment thread containing the new replies.</param>
    /// <param name="chatClient">The client-scoped AI chat client to use.</param>
    /// <param name="modelId">The model deployment identifier for the client-scoped AI connection.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A <see cref="ThreadResolutionResult" /> with <c>IsResolved = false</c> and
    ///     a <c>ReplyText</c> to post as a conversational follow-up.
    /// </returns>
    Task<ThreadResolutionResult> EvaluateConversationalReplyAsync(
        PrCommentThread thread,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default);
}
