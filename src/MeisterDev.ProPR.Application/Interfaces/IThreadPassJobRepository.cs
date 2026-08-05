// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persists thread passes and the claims that keep two of them from answering one pull request.
/// </summary>
/// <remarks>
///     Every claim here is held in the database rather than in a process, because two crawl configurations
///     over one repository and two deployed instances are both normal operating conditions.
/// </remarks>
public interface IThreadPassJobRepository
{
    /// <summary>
    ///     Persists the pass unless the pull request is already held: by a pass still in flight, or by one
    ///     that already ran for the same trigger state and would therefore repeat its work.
    /// </summary>
    /// <param name="job">The pass to persist.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>Whether the pass was persisted, and what blocked it when it was not.</returns>
    Task<TryClaimThreadPassResult> TryClaimAsync(ThreadPassJob job, CancellationToken ct = default);

    /// <summary>
    ///     Returns pending passes with attempts left whose retry delay has elapsed, oldest first.
    /// </summary>
    /// <param name="maxCount">The most passes to return.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<ThreadPassJob>> GetPendingAsync(int maxCount, CancellationToken ct = default);

    /// <summary>
    ///     Claims the pass for execution, moving it from pending to processing and spending one attempt.
    ///     Returns <c>false</c> when another executor got there first, or when the pass is still inside the
    ///     delay a failed attempt imposed, which is what stops a duplicate offer spending an attempt early.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> TryBeginAttemptAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Marks the pass completed, unless it is no longer the pass that is running: a row cancelled while
    ///     this attempt was in flight stays cancelled.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>Whether the pass was still running and therefore reached the completed status.</returns>
    Task<bool> SetCompletedAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Marks the pass skipped: terminal, having done nothing. Blocks no later pass under the same trigger,
    ///     so re-enabling whatever shut the pass out is not a silent no-op.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="reason">Why the pass did nothing, for an operator reading the row.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>Whether the pass was still running and therefore reached the skipped status.</returns>
    Task<bool> SetSkippedAsync(Guid jobId, string reason, CancellationToken ct = default);

    /// <summary>Cancels the pass while it is still in flight, and leaves a pass that already ended alone.</summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetCancelledAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Holds a pass that a budget cap already blocks, before it is claimed and before it spends anything.
    ///     Recovery is a manual restart once budget is freed, exactly as it is for a review.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="scope">The scope whose cap was reached.</param>
    /// <param name="capKind">Whether the cap was soft or hard.</param>
    /// <param name="thresholdUsd">The configured cap.</param>
    /// <param name="spentUsd">What the scope had spent.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetBudgetHeldAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks a pass stopped by a hard cap reached part-way through. Terminal: the threads it did deal with
    ///     keep their progress and the rest wait for a later pass.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="scope">The scope whose cap was reached.</param>
    /// <param name="capKind">Whether the cap was soft or hard.</param>
    /// <param name="thresholdUsd">The configured cap.</param>
    /// <param name="spentUsd">What the scope had spent.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetBudgetExceededAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a budget-blocked or terminally failed pass to pending on an operator's explicit request, with
    ///     its attempts restored. Returns <c>false</c> when the pass is missing or in a state a restart does not
    ///     apply to.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> TryRestartAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Records which connection and model the pass resolved, so its spend can be priced against the rates
    ///     the tokens were actually bought at.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="connectionId">The resolved AI connection.</param>
    /// <param name="model">The resolved model identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetAiConfigAsync(Guid jobId, Guid? connectionId, string? model, CancellationToken ct = default);

    /// <summary>Returns one pass by identifier, or <see langword="null" /> when there is none.</summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<ThreadPassJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Returns every pass recorded for one pull request, newest first.</summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="maxCount">The most passes to return.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<ThreadPassJob>> GetForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>
    ///     Records that this attempt failed. The pass returns to pending while attempts remain and reaches a
    ///     terminal failed status once they are spent, so a deterministic failure stops rather than looping.
    ///     A pass that is no longer running is left alone, so a cancellation is not overwritten by the attempt
    ///     it interrupted.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="errorMessage">Why the attempt failed.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>Whether the pass has attempts left.</returns>
    Task<bool> RecordAttemptFailureAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    ///     Cancels every pass still in flight for one pull request, and reports how many there were.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<int> CancelActiveForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Sweeps passes stuck in processing for longer than the given age. One with attempts left returns to
    ///     pending without its spent attempt refunded; one that died on its last attempt is failed terminally,
    ///     because pending it would be a row no worker ever dispatches and every later pass over the pull
    ///     request would lose the in-flight claim to it.
    /// </summary>
    /// <param name="stalledAfter">How long a pass may stay in processing before it counts as abandoned.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>How many passes were retried and how many were failed terminally.</returns>
    Task<StalledThreadPassSweep> ReclaimStalledAsync(TimeSpan stalledAfter, CancellationToken ct = default);

    /// <summary>
    ///     Returns every thread on the pull request that some pass has already acted on at this revision, with
    ///     the comment count it acted at.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="revisionKey">The stored revision key the asking pass is running at.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<ThreadPassHandledThreadKey>> GetHandledThreadKeysAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default);

    /// <summary>
    ///     Records that this pass answered or resolved one thread, at one observed comment count and one
    ///     revision. Called only after the reply or the status change was published, because a record written
    ///     first turns a provider that refused into a thread nothing ever answers again.
    /// </summary>
    /// <param name="jobId">The pass identifier.</param>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="threadId">Provider-native thread identifier.</param>
    /// <param name="observedReplyCount">The non-reviewer comment count that made the thread due.</param>
    /// <param name="revisionKey">The stored revision key the pass ran at.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task RecordHandledThreadAsync(
        Guid jobId,
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        int observedReplyCount,
        string revisionKey,
        CancellationToken ct = default);
}
