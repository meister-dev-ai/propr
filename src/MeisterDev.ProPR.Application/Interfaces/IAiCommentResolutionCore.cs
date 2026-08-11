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
    ///     Evaluates whether a code change addresses the issue raised in <paramref name="thread" />, and, when
    ///     the developer has also replied there, answers them in the same evaluation.
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
    /// <param name="hasNewReplies">
    ///     <see langword="true" /> when the thread has gained a reply nobody has answered yet, which puts the
    ///     conversation and the code change in front of the model together and obliges it to answer the person
    ///     as well as judge the finding. A thread can gain a reply and move to a new revision at the same time,
    ///     and one evaluation covers both: two evaluations would cost twice and could contradict each other.
    /// </param>
    /// <param name="evidence">
    ///     Allows the evaluation to request the diff of a file it was not supplied with, for the common case
    ///     of a finding whose fix belongs in a different file from the one the comment is anchored to.
    ///     Requests are honoured only for files this pull request changed, and only once per evaluation, so a
    ///     second model call is the maximum any thread can cost. <see langword="null" /> evaluates the diff
    ///     supplied in <paramref name="pr" /> alone and always costs exactly one call.
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
        string? outputLanguage = null,
        bool hasNewReplies = false,
        ThreadEvidenceAccess? evidence = null);

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
