// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Mentions.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persists and manages mention reply jobs.
/// </summary>
public interface IMentionReplyJobRepository
{
    /// <summary>Adds a new mention reply job.</summary>
    /// <param name="job">The job to persist.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task AddAsync(MentionReplyJob job, CancellationToken ct = default);

    /// <summary>
    ///     Adds a job unless another client's scan has already taken the same comment, returning whether
    ///     this call is the one that took it.
    /// </summary>
    /// <remarks>
    ///     The losing side of the race is an ordinary outcome rather than a fault: both clients cover the
    ///     repository, both are correct to have looked, and exactly one of them answers. Distinguishing a
    ///     lost race from a real write failure is why this returns a result instead of throwing.
    /// </remarks>
    /// <param name="job">The job to persist.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> TryAddAsync(MentionReplyJob job, CancellationToken ct = default);

    /// <summary>Returns all pending jobs, oldest first.</summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<MentionReplyJob>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    ///     Returns <c>true</c> when this comment has already been taken by some client.
    /// </summary>
    /// <remarks>
    ///     Deliberately not scoped to a client. Two clients may both cover a repository and neither can see
    ///     the other's configuration, so the question worth asking is whether this question has an answer
    ///     coming, not whether this particular client has one coming. The reviewer account addressed is part
    ///     of the identity: a mention of a different bot on the same comment is a different question.
    ///     This is an optimisation over the uniqueness rule the database enforces, not a substitute for it.
    /// </remarks>
    /// <param name="repositoryId">Provider-native repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="threadId">Provider-native thread identifier.</param>
    /// <param name="commentId">Provider-native comment identifier.</param>
    /// <param name="mentionedReviewerKey">Stable key of the reviewer account the mention addressed.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> ExistsForCommentAsync(
        string repositoryId,
        int pullRequestId,
        string threadId,
        long commentId,
        string mentionedReviewerKey,
        CancellationToken ct = default);

    /// <summary>
    ///     The provider-native identifiers of the comments ProPR itself posted on one pull request, so a scan
    ///     can tell its own answers from the questions it is looking for.
    /// </summary>
    /// <remarks>
    ///     To the provider an answer is an ordinary comment, and it is newer than every watermark. If it
    ///     repeats the reviewer's handle outside a blockquote, the next scan reads it as a new question and
    ///     answers it, and so on for as long as the pull request is open. The comparison is against the
    ///     identifiers ProPR recorded posting, not against the comment's author: on an installation whose
    ///     reviewer identity is an account a person also posts from, an author check would refuse real
    ///     questions.
    ///     Deliberately not scoped to a client, for the same reason
    ///     <see cref="ExistsForCommentAsync" /> is not: another client's answer is still ProPR's answer.
    /// </remarks>
    /// <param name="repositoryId">Provider-native repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlySet<string>> GetPostedReplyCommentIdsAsync(
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Atomic compare-and-swap on <see cref="MentionJobStatus" />.
    ///     Returns <c>false</c> if the current status does not equal <paramref name="from" />.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="from">The expected current status.</param>
    /// <param name="to">The new status to set if the current status matches.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> TryTransitionAsync(
        Guid jobId,
        MentionJobStatus from,
        MentionJobStatus to,
        CancellationToken ct = default);

    /// <summary>
    ///     Records the increment the answer is charged to and the runtime that produced it.
    /// </summary>
    /// <remarks>
    ///     Written once the answer is back, because the connection and model are only known from the response,
    ///     and they are what price the tokens. A process that dies between the call and this write leaves the
    ///     row with none of them set, so that answer's spend is unrecoverable and the retry pays for it again.
    ///     The window is one database round trip wide and closing it would need the runtime resolved before the
    ///     call rather than during it.
    /// </remarks>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="iterationId">The increment current when the answer is written, or null when unknown.</param>
    /// <param name="connectionId">The resolved AI connection, or null when none was resolved.</param>
    /// <param name="model">The resolved model identifier, or null when none was resolved.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetExecutionContextAsync(
        Guid jobId,
        int? iterationId,
        Guid? connectionId,
        string? model,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks a job as stopped by a budget cap, recording which cap stopped it.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="iterationId">
    ///     The increment resolved for the answer, or null when unknown. Recorded here because a refused answer
    ///     never reaches the write that would otherwise carry it.
    /// </param>
    /// <param name="scope">The budget scope whose cap was reached.</param>
    /// <param name="capKind">Whether the cap was soft or hard.</param>
    /// <param name="thresholdUsd">The configured cap.</param>
    /// <param name="spentUsd">What the scope had spent.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetBudgetHeldAsync(
        Guid jobId,
        int? iterationId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default);

    /// <summary>Marks a job as failed with an error message.</summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="errorMessage">A message describing the reason for the failure.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>Marks a job as successfully completed, recording the reply it posted.</summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="postedReplyCommentId">
    ///     Provider-native identifier of the reply comment the job posted, or null when the adapter reported
    ///     none. It travels on the completion update rather than in a write of its own, because a second write
    ///     is a second chance to lose it: a crash between the two leaves an answer on the pull request that
    ///     nothing can attribute afterwards.
    /// </param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetCompletedAsync(Guid jobId, string? postedReplyCommentId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the answers that reached a pull request and know their own comment id, most recently
    ///     completed first, so provenance missing for any of them can be rewritten from persisted state.
    /// </summary>
    /// <param name="completedAtOrAfter">
    ///     Oldest completion to consider. Bounds the sweep: an answer old enough that its pull request's
    ///     retained data has been purged has nothing left to attribute, and a job whose reply can never be
    ///     recorded must not be reconsidered forever.
    /// </param>
    /// <param name="maxResults">Upper bound on rows returned, a backstop rather than an expected limit.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<PostedMentionReply>> GetPostedRepliesAsync(
        DateTimeOffset completedAtOrAfter,
        int maxResults,
        CancellationToken ct = default);

    /// <summary>
    ///     Transitions all <see cref="MentionJobStatus.Processing" /> jobs back to
    ///     <see cref="MentionJobStatus.Pending" />. Called at startup to recover jobs
    ///     that were in-flight when the process last terminated.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task ResetStuckProcessingAsync(CancellationToken ct = default);
}
