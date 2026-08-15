// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Processes a single <see cref="MentionReplyJob" />: fetches full PR context,
///     generates an AI answer grounded in the PR, and posts it as a thread reply.
/// </summary>
/// <remarks>
///     <para>
///         The job opens its own budget scope. A mention arrives on its own cadence with no review job above
///         it, so there is no ambient scope to inherit, and answering without one spends money that no total
///         records and no cap observes.
///     </para>
///     <para>
///         The scope is opened here rather than inside <see cref="IMentionAnswerService" />, so a future caller
///         of that service does not silently inherit the same gap.
///     </para>
/// </remarks>
public sealed partial class MentionReplyService(
    IPullRequestFetcher pullRequestFetcher,
    IMentionReplyJobRepository jobRepository,
    IMentionAnswerService answerService,
    IScmProviderRegistry providerRegistry,
    ILogger<MentionReplyService> logger,
    IProviderActivationService? providerActivationService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IProtocolRecorder? protocolRecorder = null,
    IPullRequestIterationResolver? iterationResolver = null,
    IBudgetCapsProvider? budgetCapsProvider = null,
    IReviewSpendAccumulator? spendAccumulator = null,
    IBudgetScopeAccessor? budgetScopeAccessor = null,
    IBudgetEventPublisher? budgetEventPublisher = null) : IMentionReplyService
{
    /// <summary>
    ///     What a developer is told when their question was not answered because the client's budget is used
    ///     up. Silence would read as the reviewer ignoring them, and they cannot fix the cause themselves, so
    ///     the note names who can.
    /// </summary>
    private const string BudgetExhaustedReply =
        "This client's review budget for the current period is used up, so the question was not answered. " +
        "An administrator can raise the cap in ProPR.";

    /// <inheritdoc />
    public async Task ProcessAsync(MentionReplyJob job, CancellationToken cancellationToken = default)
    {
        // Atomic claim: transition Pending → Processing before doing expensive work.
        var claimed = await jobRepository.TryTransitionAsync(
            job.Id,
            MentionJobStatus.Pending,
            MentionJobStatus.Processing,
            cancellationToken);

        if (!claimed)
        {
            LogJobAlreadyClaimed(logger, job.Id);
            return;
        }

        // Held outside the try so the handlers below can read the breach off it. The enforcing chat client
        // records a reached hard cap on the scope before it throws, which is what lets a budget cut still be
        // recognized as one when an intervening layer wraps the exception.
        BudgetScope? budgetScope = null;

        try
        {
            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(job.Provider, cancellationToken))
            {
                await jobRepository.SetFailedAsync(
                    job.Id,
                    "The provider family is currently disabled by system administration.",
                    cancellationToken);
                return;
            }

            // Resolved before the scope is built, because the increment is one of the three scopes a cap is
            // measured against and the row has to carry it either way.
            var iterationId = await this.TryResolveIterationAsync(job, cancellationToken);
            job.SetIteration(iterationId);

            budgetScope = await this.TryCreateBudgetScopeAsync(job, cancellationToken);
            if (budgetScope is not null && FindHardCapBreach(budgetScope) is { } hardCapBreach)
            {
                await this.HandleBudgetBlockAsync(job, hardCapBreach, cancellationToken);
                return;
            }

            if (budgetScope is not null && FindSoftCapBreach(budgetScope) is { } softCapBreach)
            {
                await this.AnnounceSoftCapAsync(job, softCapBreach, cancellationToken);
            }

            using var budgetScopeHandle = budgetScope is null
                ? null
                : budgetScopeAccessor!.BeginScope(budgetScope);

            await this.AnswerAsync(job, iterationId, cancellationToken);
        }
        catch (BudgetHardCapReachedException ex)
        {
            // The cap was reached by this answer's own call, so the developer is told the same thing they
            // would have been told had it been reached beforehand.
            await this.HandleBudgetBlockAsync(job, ex.Breach, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A wrapped budget cut is still a budget cut. Reporting it as a failure would tell the developer
            // nothing and would leave the reached cap unrecorded.
            if (budgetScope?.TrippedBreach is { } trippedBreach)
            {
                await this.HandleBudgetBlockAsync(job, trippedBreach, cancellationToken);
                return;
            }

            LogJobFailed(logger, job.Id, ex);

            await jobRepository.SetFailedAsync(
                job.Id,
                ex.Message,
                cancellationToken);
        }
    }

    /// <summary>
    ///     The hard cap this answer would cross, if any. Only a hard cap refuses a mention.
    /// </summary>
    /// <remarks>
    ///     A review is held at admission by a soft cap as well, because a soft cap means no new work is
    ///     admitted and a review is new work. An answer is not: someone is already in the conversation and is
    ///     waiting. Refusing on a soft cap would also be permanent here, because a refused answer is terminal
    ///     and the same question cannot be asked twice, so one crossed warning threshold would silence every
    ///     mention on the installation until an administrator intervened.
    /// </remarks>
    private static BudgetBreach? FindHardCapBreach(BudgetScope budgetScope)
    {
        return BudgetEvaluator.FindHardCapBreach(
            budgetScope.Caps,
            budgetScope.Baseline.ClientMonthToDate.KnownUsd,
            budgetScope.Baseline.PullRequest.KnownUsd,
            budgetScope.Baseline.Increment.KnownUsd);
    }

    /// <summary>The soft cap this answer is being written past, if any. It reports rather than refuses.</summary>
    private static BudgetBreach? FindSoftCapBreach(BudgetScope budgetScope)
    {
        return BudgetEvaluator.FindSoftCapBreach(
            budgetScope.Caps,
            budgetScope.Baseline.ClientMonthToDate.KnownUsd,
            budgetScope.Baseline.PullRequest.KnownUsd);
    }

    private async Task AnswerAsync(MentionReplyJob job, int? iterationId, CancellationToken cancellationToken)
    {
        // Fetch full PR context (iterationId = 1 is sufficient for existing threads).
        var pullRequest = await pullRequestFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            1,
            null,
            job.ClientId,
            cancellationToken);

        // Generate an AI answer grounded in the PR, focused on the specific thread.
        var answer = await answerService.AnswerAsync(
            pullRequest,
            job.ClientId,
            job.MentionText,
            job.ThreadId,
            cancellationToken);

        // What the tokens were bought through is stored before the spend is recorded, because pricing reads
        // it off the row.
        await jobRepository.SetExecutionContextAsync(
            job.Id,
            iterationId,
            answer.ConnectionId,
            answer.ModelId,
            cancellationToken);

        // Recorded before publication, because the tokens are spent whether or not the reply reaches the
        // pull request. A publication failure that also lost the spend would leave a client billed for
        // nothing anybody can see.
        await this.RecordSpendAsync(job, answer, cancellationToken);

        // Post the reply to the ADO thread.
        var replyCommentId = await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
            .ReplyAsync(job.ClientId, job.ReviewThreadReference, answer.Text, cancellationToken);

        // Completing the job carries the comment id that was just posted. Nothing that can throw may sit
        // between posting the answer and completing the job: a cancellation in that gap leaves the answer
        // on the pull request and the job back in Pending at the next startup, which posts it a second
        // time. So the id rides along on the completion update rather than travelling in a write of its own.
        await jobRepository.SetCompletedAsync(job.Id, replyCommentId, cancellationToken);
        LogJobCompleted(logger, job.Id);

        // Provenance last, because it is bookkeeping and the gap above must stay empty. It is no longer the
        // only chance to record it: the id is on the completed job, so a provenance row lost to a crash
        // here is derivable from persisted state and IMentionReplyProvenanceReconciler rewrites it.
        await this.RecordPostedReplyOriginAsync(job, replyCommentId, cancellationToken);
    }

    /// <summary>
    ///     Opens the budget scope this answer is metered and capped against, or <see langword="null" /> when
    ///     the client has no caps configured or the budgeting services are absent.
    /// </summary>
    private async Task<BudgetScope?> TryCreateBudgetScopeAsync(MentionReplyJob job, CancellationToken ct)
    {
        if (budgetScopeAccessor is null || budgetCapsProvider is null || spendAccumulator is null)
        {
            return null;
        }

        // An installation without the Budgeting capability reports no caps here, so an unlicensed client is
        // metered without ever being held.
        var caps = await budgetCapsProvider.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyConfigured)
        {
            return null;
        }

        var baseline = await spendAccumulator.GetBaselineAsync(
            ReviewSpendSubject.For(job),
            DateOnly.FromDateTime(DateTime.UtcNow),
            ct);
        return new BudgetScope(caps, baseline);
    }

    /// <summary>
    ///     The increment the answer is charged to: whichever one is current when it is written. A lookup that
    ///     fails yields <see langword="null" />, which counts the answer against the whole pull request rather
    ///     than against one increment, so an unreadable revision cannot become a way past an increment cap.
    /// </summary>
    private async Task<int?> TryResolveIterationAsync(MentionReplyJob job, CancellationToken ct)
    {
        if (iterationResolver is null)
        {
            return null;
        }

        try
        {
            return await iterationResolver.GetLatestIterationIdAsync(
                job.ClientId,
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLatestRevisionLookupFailed(logger, job.Id, ex);
            return null;
        }
    }

    /// <summary>
    ///     Ends the job on a reached cap: the cap that stopped it is recorded, the developer is told why nothing
    ///     was answered, and a budget event is published.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Terminal rather than held for a later restart, unlike a review. Someone who still wants an answer
    ///         asks again once the budget is raised.
    ///     </para>
    ///     <para>
    ///         The status is written before the note, which is the opposite of the order the answer path uses,
    ///         because the two paths fail in opposite directions. An answer lost to a crash before its
    ///         completion write is worth repeating; a refusal is not. A job left in Processing returns to
    ///         Pending at the next startup, and a note written before the status would then be posted again on
    ///         every restart. Writing the status first costs at worst the note, which is already best-effort.
    ///     </para>
    ///     <para>
    ///         Nothing here may throw. It runs from inside a catch clause on one of its two paths, where an
    ///         escaping exception would leave the job in Processing, and from inside the try on the other, where
    ///         it would be caught and overwrite the status just recorded with a failure.
    ///     </para>
    /// </remarks>
    private async Task HandleBudgetBlockAsync(MentionReplyJob job, BudgetBreach breach, CancellationToken ct)
    {
        LogAnswerHeldByBudget(logger, job.Id, breach.Scope, breach.ThresholdUsd, breach.SpentUsd);

        try
        {
            // The increment is persisted here too, because the refused path returns before the answer would
            // have written it. Without this the row reports no increment while the budget event names one.
            await jobRepository.SetBudgetHeldAsync(
                job.Id,
                job.IterationId,
                breach.Scope,
                breach.CapKind,
                breach.ThresholdUsd,
                breach.SpentUsd,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Recorded nowhere, so the job stays in Processing and is retried from Pending after a restart.
            // Saying nothing now is what keeps that retry from being a second note on the thread.
            LogBudgetBlockRecordingFailed(logger, job.Id, ex);
            return;
        }

        await this.PostBudgetNoticeAsync(job, ct);

        if (budgetEventPublisher is null)
        {
            return;
        }

        try
        {
            // Built directly rather than through FromBreach, whose increment is not optional. An answer whose
            // increment could not be resolved reports none, because 0 is an increment number that means
            // something and this one is simply not known.
            await budgetEventPublisher.PublishAsync(
                new BudgetEventNotification(
                    job.ClientId,
                    breach.CapKind == BudgetCapKind.Hard
                        ? BudgetEventType.HardCapReached
                        : BudgetEventType.SoftCapReached,
                    breach.Scope,
                    breach.ThresholdUsd,
                    breach.SpentUsd,
                    job.Id,
                    job.PullRequestId,
                    job.IterationId),
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogBudgetEventPublishFailed(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Records that this answer is being written past a soft cap, without withholding it.
    /// </summary>
    /// <remarks>
    ///     A soft cap that stops nothing still has to be visible, or a client crosses its warning threshold
    ///     entirely through mention spend and nobody is told. Best-effort throughout: the answer is owed to a
    ///     person and an alert that could not be raised is no reason to withhold it.
    /// </remarks>
    private async Task AnnounceSoftCapAsync(MentionReplyJob job, BudgetBreach breach, CancellationToken ct)
    {
        LogAnswerPastSoftCap(logger, job.Id, breach.Scope, breach.ThresholdUsd, breach.SpentUsd);

        if (budgetEventPublisher is null)
        {
            return;
        }

        try
        {
            await budgetEventPublisher.PublishAsync(
                new BudgetEventNotification(
                    job.ClientId,
                    BudgetEventType.SoftCapReached,
                    breach.Scope,
                    breach.ThresholdUsd,
                    breach.SpentUsd,
                    job.Id,
                    job.PullRequestId,
                    job.IterationId),
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogBudgetEventPublishFailed(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Tells the developer their question went unanswered because the budget is used up. Best-effort: a
    ///     provider that refuses the reply must still leave the job recorded as stopped by a cap.
    /// </summary>
    private async Task PostBudgetNoticeAsync(MentionReplyJob job, CancellationToken ct)
    {
        try
        {
            await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                .ReplyAsync(job.ClientId, job.ReviewThreadReference, BudgetExhaustedReply, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogBudgetNoticeNotPosted(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Moves what the answer spent onto the job's own totals and the client's daily usage sample, through
    ///     the trace record that owns it.
    /// </summary>
    /// <remarks>
    ///     Strictly best-effort. The tokens are already spent and the developer is waiting on an answer;
    ///     accounting that fails costs a number, while refusing to answer because of it costs the work.
    /// </remarks>
    private async Task RecordSpendAsync(MentionReplyJob job, MentionAnswer answer, CancellationToken ct)
    {
        if (protocolRecorder is null)
        {
            return;
        }

        Guid protocolId;
        try
        {
            protocolId = await protocolRecorder.BeginForMentionReplyAsync(
                job.Id,
                $"mention-thread-{job.ThreadId}",
                answer.ModelId,
                ct,
                answer.LogicalModelName);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogProtocolBeginFailed(logger, job.Id, ex);
            return;
        }

        try
        {
            await protocolRecorder.RecordAiCallAsync(
                protocolId,
                1,
                answer.Usage.InputTokens,
                answer.Usage.OutputTokens,
                null,
                null,
                answer.Text,
                ct,
                cachedInputTokens: answer.Usage.CachedInputTokens,
                cacheWriteTokens: answer.Usage.CacheWriteTokens,
                reasoningTokens: answer.Usage.ReasoningTokens);

            // Closing the record is what moves the tokens onto the job row and the client's usage.
            await protocolRecorder.SetCompletedAsync(
                protocolId,
                "completed",
                answer.Usage.InputTokens,
                answer.Usage.OutputTokens,
                1,
                0,
                null,
                ct,
                answer.Usage.CachedInputTokens,

                // Only a non-zero cache count is evidence the provider reported one. Normalized usage carries
                // plain zeros, so "no cache tokens" and "no cache reporting" are the same value here and
                // claiming either way from it would be an assertion the number cannot support.
                answer.Usage.CachedInputTokens > 0 || answer.Usage.CacheWriteTokens > 0
                    ? CacheObservabilityStatus.Observable
                    : CacheObservabilityStatus.Unknown,
                answer.Usage.CacheWriteTokens,
                answer.Usage.ReasoningTokens);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSpendRecordingFailed(logger, protocolId, ex);
        }
    }

    // A mention answer is a comment ProPR authored, so it carries provenance like any other. Without a row it
    // reads back as a human comment wherever no token identity is resolvable, and the thread it sits on is
    // misattributed. The mention job is the originating job, so its own id is what the row records.
    //
    // Strictly best-effort: the answer is already on the pull request by the time this runs, and a recording
    // failure must neither undo it nor fail the job. An adapter that reported no comment id records nothing,
    // and has nothing to recover either: there is no id to attribute the comment by.
    //
    // Best-effort no longer means one attempt. The completed job carries the comment id, so a failure here, or
    // a process death before it runs, leaves the row derivable rather than lost, and the reconciler writes it.
    private async Task RecordPostedReplyOriginAsync(
        MentionReplyJob job,
        string? providerCommentId,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null || string.IsNullOrWhiteSpace(providerCommentId))
        {
            return;
        }

        try
        {
            await postedCommentOriginStore.RecordAsync(
                [
                    new PostedCommentOriginEntry(
                        job.ClientId,
                        job.RepositoryId,
                        job.PullRequestId,
                        job.ReviewThreadReference.ExternalThreadId,
                        providerCommentId,
                        job.Id,
                        DateTimeOffset.UtcNow),
                ],
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedCommentOriginRecordingFailed(logger, job.Id, ex);
        }
    }
}
