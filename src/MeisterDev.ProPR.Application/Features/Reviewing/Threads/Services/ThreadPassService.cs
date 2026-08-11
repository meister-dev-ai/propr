// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Ports;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;

/// <summary>
///     Runs one thread pass over a pull request: judges which reviewer-owned threads the developer has fixed,
///     answers the ones they replied to, and records how far it got.
/// </summary>
/// <remarks>
///     <para>
///         Progress advances per thread and only for threads this pass actually acted on, so a failure part-way
///         leaves the rest for the next cycle. The pull-request watermark advances only when every thread the
///         pass saw was dealt with, because it is the record that this revision needs no further visit.
///     </para>
///     <para>
///         The pass opens its own budget scope. It runs on its own cadence with no review job above it, so there
///         is no ambient scope to inherit, and a pass that inherited one would meter its calls against another
///         unit of work's total.
///     </para>
/// </remarks>
public sealed partial class ThreadPassService(
    IThreadPassJobRepository threadPassJobs,
    IReviewPrScanThreadPassStore prScans,
    IPullRequestFetcher pullRequestFetcher,
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    IAiCommentResolutionCore resolutionCore,
    IProtocolRecorder protocolRecorder,
    ILogger<ThreadPassService> logger,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IProviderActivationService? providerActivationService = null,
    IPullRequestIterationResolver? iterationResolver = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IBudgetCapsProvider? budgetCapsProvider = null,
    IReviewSpendAccumulator? spendAccumulator = null,
    IBudgetScopeAccessor? budgetScopeAccessor = null,
    IBudgetEventPublisher? budgetEventPublisher = null) : IThreadPassService
{
    private const string ResolvedThreadStatus = "fixed";

    /// <inheritdoc />
    public async Task ProcessAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var budgetScope = await this.TryCreateBudgetScopeAsync(job, ct);
        if (budgetScope is not null && FindAdmissionBreach(budgetScope) is { } admissionBreach)
        {
            // Held rather than run, exactly as a queued review is: the attempt is not spent, no model call is
            // made, and the pass waits on an operator freeing budget and restarting it.
            LogPassHeldByBudget(
                logger,
                job.Id,
                admissionBreach.Scope,
                admissionBreach.ThresholdUsd,
                admissionBreach.SpentUsd);
            await threadPassJobs.SetBudgetHeldAsync(
                job.Id,
                admissionBreach.Scope,
                admissionBreach.CapKind,
                admissionBreach.ThresholdUsd,
                admissionBreach.SpentUsd,
                ct);
            await this.EmitBudgetEventAsync(job, admissionBreach, ct);
            return;
        }

        if (!await threadPassJobs.TryBeginAttemptAsync(job.Id, ct))
        {
            LogPassAlreadyClaimed(logger, job.Id);
            return;
        }

        using var budgetScopeHandle = budgetScope is null ? null : budgetScopeAccessor!.BeginScope(budgetScope);

        try
        {
            await this.RunAsync(job, ct);
        }
        catch (BudgetHardCapReachedException ex)
        {
            await this.HandleBudgetCutAsync(job, ex.Breach, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (budgetScope?.TrippedBreach is { } breach)
            {
                await this.HandleBudgetCutAsync(job, breach, ct);
                return;
            }

            var retryable = await threadPassJobs.RecordAttemptFailureAsync(job.Id, ex.Message, ct);
            LogPassAttemptFailed(logger, job.Id, job.PullRequestId, retryable, ex);
        }
    }

    private static BudgetBreach? FindAdmissionBreach(BudgetScope budgetScope)
    {
        return BudgetEvaluator.FindAdmissionBreach(
            budgetScope.Caps,
            budgetScope.Baseline.ClientMonthToDate.KnownUsd,
            budgetScope.Baseline.PullRequest.KnownUsd,
            budgetScope.Baseline.Increment.KnownUsd);
    }

    private async Task<BudgetScope?> TryCreateBudgetScopeAsync(ThreadPassJob job, CancellationToken ct)
    {
        if (budgetScopeAccessor is null || budgetCapsProvider is null || spendAccumulator is null)
        {
            return null;
        }

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

    private async Task HandleBudgetCutAsync(ThreadPassJob job, BudgetBreach breach, CancellationToken ct)
    {
        // Terminal, as a cut review is. Threads this pass already answered keep their per-thread progress, and
        // the watermark stays where it was, so the rest are picked up by a later pass.
        LogPassCutByBudget(logger, job.Id, breach.Scope, breach.ThresholdUsd, breach.SpentUsd);
        await threadPassJobs.SetBudgetExceededAsync(
            job.Id,
            breach.Scope,
            breach.CapKind,
            breach.ThresholdUsd,
            breach.SpentUsd,
            ct);
        await this.EmitBudgetEventAsync(job, breach, ct);
    }

    private async Task EmitBudgetEventAsync(ThreadPassJob job, BudgetBreach breach, CancellationToken ct)
    {
        if (budgetEventPublisher is null)
        {
            return;
        }

        await budgetEventPublisher.PublishAsync(
            BudgetEventNotification.FromBreach(breach, job.ClientId, job.Id, job.PullRequestId, job.IterationId),
            ct);
    }

    private async Task RunAsync(ThreadPassJob job, CancellationToken ct)
    {
        if (providerActivationService is not null
            && !await providerActivationService.IsEnabledAsync(job.Provider, ct))
        {
            await threadPassJobs.SetSkippedAsync(job.Id, "The provider was deactivated after the pass was queued.", ct);
            return;
        }

        // The gates are re-read here rather than trusted from the trigger: a client may have switched thread
        // interaction off between the pass being queued and being run, and that answer has to hold. The pass
        // is skipped rather than completed, because it did nothing: a completed pass would block the identical
        // trigger and make switching thread interaction back on a silent no-op.
        var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
        var capabilities = providerRegistry.GetRegisteredCapabilities(job.Provider);
        if (behavior == CommentResolutionBehavior.Disabled
            || !ReviewThreadCapabilities.Advertises(capabilities, ReviewThreadCapabilities.Status))
        {
            await threadPassJobs.SetSkippedAsync(
                job.Id,
                "Thread interaction was switched off, or the provider stopped advertising thread status.",
                ct);
            return;
        }

        var canReply = ReviewThreadCapabilities.Advertises(capabilities, ReviewThreadCapabilities.Reply);

        // Read before the pull request, because it determines what the pull request fetch has to request.
        // Only a pass that evaluates code needs the changed-file names, and a pass with only replies to
        // answer should not spend a provider call listing files nothing will read.
        var scan = await prScans.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
        var revisionMoved = !string.Equals(
            scan?.LastThreadPassRevisionKey,
            job.RevisionKey,
            StringComparison.Ordinal);

        // Threads, pull-request metadata, and on a moved revision the names of the files that changed.
        // Downloading the content of every changed file to determine whether a thread needs answering is
        // what made this pass a per-push, per-pull-request bulk read; the diffs a code-change evaluation
        // needs are retrieved per thread. The manifest holds names only, and it is what allows an evaluation
        // to distinguish a fix that was never made from one in a file it was not supplied with.
        var pullRequest = await this.FetchThreadContextAsync(job, revisionMoved, ct);

        // A pass queued before the pull request closed must not go on answering it. This is also what keeps a
        // pass rehydrated after a restart from speaking for a pull request that ended while the process was down.
        if (pullRequest.Status != PrStatus.Active)
        {
            LogPassSkippedForInactivePullRequest(logger, job.Id, job.PullRequestId, pullRequest.Status.ToString());
            await threadPassJobs.SetSkippedAsync(
                job.Id,
                $"The pull request was {pullRequest.Status} when the pass ran.",
                ct);
            return;
        }

        // An unreadable thread list is not an empty one. Treating it as empty would record the pull request
        // as checked at this revision and retire every stored per-thread row, so the next synchronization
        // would replay every already-resolved thread as freshly resolved.
        if (pullRequest.ExistingThreads is null)
        {
            var readAttemptsRemain = await threadPassJobs.RecordAttemptFailureAsync(
                job.Id,
                "The pull request's comment threads could not be read, so nothing is known about them.",
                ct);
            LogThreadsUnreadable(logger, job.Id, job.PullRequestId, readAttemptsRemain);
            return;
        }

        // Checked before anything is judged, because a pass fetches at the revision it was queued at: a push
        // between queueing and execution would otherwise have it spend a model call on code the developer has
        // already replaced. Skipped rather than failed, so the next tick queues a pass at the revision that
        // is actually current.
        if (await this.IsSupersededAsync(job, ct))
        {
            LogPassSkippedForMovedRevision(logger, job.Id, job.PullRequestId, job.IterationId);
            await threadPassJobs.SetSkippedAsync(
                job.Id,
                "The pull request moved to a later revision before the pass ran.",
                ct);
            return;
        }

        var ownership = await this.ResolveThreadOwnershipAsync(job, pullRequest, ct);
        var reviewerThreads = GetReviewerThreads(pullRequest, ownership.Resolver);

        var handled = await threadPassJobs.GetHandledThreadKeysAsync(
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            job.RevisionKey,
            ct);
        var handledKeys = handled.ToHashSet();

        var runtime = reviewerThreads.Count == 0 ? null : await this.TryResolveRuntimeAsync(job, ct);

        // Built once for the whole pass, so that several threads requesting the same file, which findings on
        // one interface commonly do, cost one provider retrieval between them rather than one each.
        var evidence = runtime is null ? null : this.BuildEvidenceAccess(job, runtime);

        var allThreadsDealtWith = true;
        var observedByThreadId = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var thread in reviewerThreads)
        {
            var threadId = thread.ThreadId!;
            var observed = CountNonReviewerComments(thread, ownership.Resolver);
            observedByThreadId[threadId] = observed;

            // A pull request that closed part-way through the pass has already had its row cancelled, and
            // going on would answer threads on a pull request nobody is reading. One indexed row read per
            // thread against one model call per thread is a price worth paying.
            if (!await this.IsStillRunningAsync(job, ct))
            {
                LogPassAbandonedAfterCancellation(logger, job.Id, job.PullRequestId);
                return;
            }

            try
            {
                await this.HandleThreadAsync(
                    job,
                    pullRequest,
                    thread,
                    threadId,
                    scan,
                    observed,
                    revisionMoved,
                    behavior,
                    canReply,
                    handledKeys,
                    runtime,
                    evidence,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not BudgetHardCapReachedException)
            {
                // One thread that could not be dealt with holds back only the watermark. Every thread this
                // pass did answer keeps its progress, so the next cycle retries the remainder rather than
                // starting the pull request over. A reached hard cap ends the whole pass instead, because
                // retrying thread by thread against a cap that will not move buys nothing; it is named here as
                // well as ridden in on the cancellation channel, so widening this filter cannot resurrect it.
                allThreadsDealtWith = false;
                LogThreadHandlingFailed(logger, threadId, job.PullRequestId, ex);
            }
        }

        if (allThreadsDealtWith)
        {
            // A thread the pass no longer sees is one the provider no longer reports as reviewer-owned, and
            // its stored counters would otherwise outlive it. Only a complete ownership answer justifies the
            // deletion: with the provenance lookup degraded, threads ProPR does own can read as someone
            // else's, and the row that would go carries the previous-status half of the resolved transition,
            // which belongs to the thread-memory state machine rather than to this pass.
            if (ownership.IsComplete)
            {
                await prScans.RetainOnlyThreadsAsync(
                    job.ClientId,
                    job.RepositoryId,
                    job.PullRequestId,
                    observedByThreadId.Keys,
                    ct);
            }

            await prScans.SetThreadPassWatermarkAsync(
                job.ClientId,
                job.RepositoryId,
                job.PullRequestId,
                job.RevisionKey,
                ct);

            await threadPassJobs.SetCompletedAsync(job.Id, ct);
            LogPassCompleted(logger, job.Id, job.PullRequestId, reviewerThreads.Count);
            return;
        }

        var attemptsRemain = await threadPassJobs.RecordAttemptFailureAsync(
            job.Id,
            "At least one thread could not be handled; the watermark stays where it was so the rest are retried.",
            ct);
        LogPassPartiallyHandled(logger, job.Id, job.PullRequestId, attemptsRemain);
    }

    /// <summary>
    ///     Whether the pull request has moved to a later revision than the one this pass was queued at. A pass
    ///     that judges the previous revision spends a model call on code the developer has already replaced,
    ///     and answers a thread against a diff nobody is looking at any more.
    /// </summary>
    private async Task<bool> IsSupersededAsync(ThreadPassJob job, CancellationToken ct)
    {
        if (iterationResolver is null)
        {
            return false;
        }

        try
        {
            var latestIterationId = await iterationResolver.GetLatestIterationIdAsync(
                job.ClientId,
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                ct);
            return latestIterationId > job.IterationId;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Not knowing the current revision is no reason to refuse to answer the conversation.
            LogLatestRevisionLookupFailed(logger, job.Id, ex);
            return false;
        }
    }

    /// <summary>Whether the pass's own row still says it is the pass that is running.</summary>
    private async Task<bool> IsStillRunningAsync(ThreadPassJob job, CancellationToken ct)
    {
        var stored = await threadPassJobs.GetByIdAsync(job.Id, ct);
        return stored is null || stored.Status == ThreadPassJobStatus.Processing;
    }

    private async Task HandleThreadAsync(
        ThreadPassJob job,
        PullRequest pullRequest,
        PrCommentThread thread,
        string threadId,
        ReviewPrScan? scan,
        int observed,
        bool revisionMoved,
        CommentResolutionBehavior behavior,
        bool canReply,
        HashSet<ThreadPassHandledThreadKey> handledKeys,
        ThreadPassRuntime? runtime,
        ThreadEvidenceAccess? evidence,
        CancellationToken ct)
    {
        if (IsResolvedStatus(thread.Status))
        {
            return;
        }

        var storedCount = scan?.Threads
            .FirstOrDefault(candidate => string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal))
            ?.LastSeenReplyCount ?? 0;
        var hasNewReplies = observed > storedCount;
        if (!revisionMoved && !hasNewReplies)
        {
            return;
        }

        // The thread, the comment count that made it due and the revision the pass runs at are what identify
        // this unit of work. Work already recorded under that triple was published by an attempt that may have
        // died before it could advance the counter, so it is neither judged again nor answered again. Without
        // the revision an unanswered finding, whose comment count never moves, would be judged once and then
        // skipped on every later push.
        if (handledKeys.Contains(new ThreadPassHandledThreadKey(threadId, observed, job.RevisionKey)))
        {
            return;
        }

        if (runtime is null)
        {
            throw new InvalidOperationException(
                $"No AI runtime is configured for client {job.ClientId}, so pull request #{job.PullRequestId} cannot have its threads evaluated.");
        }

        var outputLanguage = await clientRegistry.GetOutputLanguageAsync(job.ClientId, ct);

        var evaluationKind = DescribeEvaluation(revisionMoved, hasNewReplies);
        var protocolId = await this.BeginThreadProtocolAsync(job, threadId, evaluationKind, runtime, ct);

        // One evaluation per thread, whichever of the two conditions woke it up. A moved revision brings the
        // code delta, an unanswered reply brings the conversation, and a thread carrying both is judged and
        // answered in the same call: two calls would cost twice and could return contradictory verdicts. The
        // delta is fetched only where there is a new one, so the conversation-only case costs what it always did.
        var resolution = revisionMoved
            ? await resolutionCore.EvaluateCodeChangeAsync(
                thread,
                await this.WithThreadFileAsync(job, pullRequest, thread, ct),
                runtime.ChatClient,
                runtime.ModelId,
                ct,
                outputLanguage,
                hasNewReplies,
                evidence)
            : await resolutionCore.EvaluateConversationalReplyAsync(
                thread,
                runtime.ChatClient,
                runtime.ModelId,
                ct,
                outputLanguage);

        // Closed as soon as the answer is in, because the tokens are spent whether or not publication then
        // succeeds. Closing the protocol is what moves them onto the pass's totals and the client's usage.
        await this.RecordEvaluationProtocolAsync(protocolId, resolution, ct);

        await this.ApplyResolvedThreadActionAsync(
            job,
            thread,
            threadId,
            behavior,
            resolution,
            canReply,
            hasNewReplies,
            ct);

        // Recorded after publication, never before. A record written first turns a provider that returned 429
        // or refused a locked thread into a thread that is never answered and never retried, which is the
        // opposite of what progress state is for.
        await threadPassJobs.RecordHandledThreadAsync(
            job.Id,
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            threadId,
            observed,
            job.RevisionKey,
            ct);

        await prScans.SetLastSeenReplyCountsAsync(
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            new Dictionary<string, int>(StringComparer.Ordinal) { [threadId] = observed },
            ct);
    }

    /// <summary>
    ///     Reads the conversation, requesting the changed-file names when a code change is to be evaluated.
    /// </summary>
    /// <remarks>
    ///     The names are supplementary, and a pass that cannot obtain them still has threads to answer. Every
    ///     provider reports an incomplete listing by throwing, and on a large pull request that is the most
    ///     likely failure, so a manifest that fails to load falls back to the conversation-only read rather
    ///     than costing the pass every thread it would otherwise have handled.
    /// </remarks>
    private async Task<PullRequest> FetchThreadContextAsync(
        ThreadPassJob job,
        bool includeChangedFileManifest,
        CancellationToken ct)
    {
        if (includeChangedFileManifest)
        {
            try
            {
                return await pullRequestFetcher.FetchThreadContextAsync(
                    job.OrganizationUrl,
                    job.ProjectId,
                    job.RepositoryId,
                    job.PullRequestId,
                    job.IterationId,
                    clientId: job.ClientId,
                    cancellationToken: ct,
                    includeChangedFileManifest: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogChangedFileManifestUnavailable(logger, job.Id, job.PullRequestId, ex);
            }
        }

        return await pullRequestFetcher.FetchThreadContextAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            clientId: job.ClientId,
            cancellationToken: ct);
    }

    /// <summary>
    ///     Adds the diff of the one file a thread is anchored to, fetched for that file alone. A thread with
    ///     no file, or a file the provider has nothing to say about, gets the pull request as it came.
    /// </summary>
    private async Task<PullRequest> WithThreadFileAsync(
        ThreadPassJob job,
        PullRequest pullRequest,
        PrCommentThread thread,
        CancellationToken ct)
    {
        if (thread.FilePath is null)
        {
            return pullRequest;
        }

        // Read before the substitution below, because a pull request that arrived without a manifest derives
        // one from its changed files. Replacing those with the anchor file alone would reduce the manifest to
        // that same file, restoring the omission this path exists to correct.
        var changedFileManifest = pullRequest.AllPrFileSummaries;

        try
        {
            var file = await pullRequestFetcher.FetchFileDiffAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                thread.FilePath,
                clientId: job.ClientId,
                cancellationToken: ct);

            // Presented under the path the provider put on the thread. Azure DevOps anchors a thread to a
            // repo-root-absolute path while a changed file carries the repo-relative one, and the prompt
            // matches the two by string, so the fetched diff has to arrive under the name the thread uses.
            return file is null
                ? pullRequest
                : pullRequest with
                {
                    ChangedFiles = [file with { Path = thread.FilePath }],
                    AllChangedFileSummaries = changedFileManifest,
                };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The conversation is still answerable without the diff, and saying so is better than refusing.
            LogThreadFileLookupFailed(logger, job.Id, thread.ThreadId ?? string.Empty, ex);
            return pullRequest;
        }
    }

    /// <summary>
    ///     Allows one thread's evaluation to request the diff of a file it was not supplied with, bounded by
    ///     the context window of the model performing the evaluation.
    /// </summary>
    /// <remarks>
    ///     A comment is anchored to the location where a problem was observed, which is often not the
    ///     location that has to change. Without this, a fix in another file is absent from the evaluation by
    ///     construction, and the only accurate result the model can return is that the change is not visible
    ///     to it, however many times the developer pushes it. Which files may be requested is determined
    ///     against the pull request's own changed-file manifest, so no model-generated string reaches the
    ///     provider as a path.
    /// </remarks>
    private ThreadEvidenceAccess BuildEvidenceAccess(ThreadPassJob job, ThreadPassRuntime runtime)
    {
        // One entry per path for the lifetime of the pass, holding the result that was returned including
        // the absence of one. Findings raised on a single interface commonly refer to the same
        // implementation, and retrieving it again per thread would spend provider calls on content the pass
        // already holds.
        var fetchedByPath = new Dictionary<string, ChangedFile?>(StringComparer.Ordinal);

        return new ThreadEvidenceAccess(
            async (path, token) =>
            {
                if (fetchedByPath.TryGetValue(path, out var cached))
                {
                    return cached;
                }

                ChangedFile? file;
                try
                {
                    file = await pullRequestFetcher.FetchFileDiffAsync(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        job.PullRequestId,
                        job.IterationId,
                        path,
                        clientId: job.ClientId,
                        cancellationToken: token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Evaluated without it, because leaving the developer without an answer is a worse
                    // outcome than one missing file. Not cached, so a later thread may still retrieve it.
                    LogRequestedFileLookupFailed(logger, job.Id, path, ex);
                    return null;
                }

                fetchedByPath[path] = file;
                return file;
            },
            runtime.MaxContextTokens,
            runtime.TokenizerName,
            path => LogRequestedFileOutsidePullRequest(logger, job.Id, path));
    }

    private async Task ApplyResolvedThreadActionAsync(
        ThreadPassJob job,
        PrCommentThread thread,
        string threadId,
        CommentResolutionBehavior behavior,
        ThreadResolutionResult resolution,
        bool canReply,
        bool hasNewReplies,
        CancellationToken ct)
    {
        var resolvedAction = BuildResolvedThreadAction(
            threadId,
            thread,
            behavior,
            resolution,
            canReply,
            hasNewReplies);
        ReviewThreadRef? repliedThread = null;
        string? repliedCommentId = null;

        if (resolvedAction.ShouldPostReply && resolvedAction.ReplyText is not null)
        {
            repliedThread = CreateReviewThreadRef(job, thread, threadId);
            repliedCommentId = await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                .ReplyAsync(job.ClientId, repliedThread, resolvedAction.ReplyText, ct);
        }

        if (resolvedAction.ShouldResolveThread)
        {
            await providerRegistry.GetReviewThreadStatusWriter(job.Provider)
                .UpdateThreadStatusAsync(
                    job.ClientId,
                    CreateReviewThreadRef(job, thread, threadId),
                    ResolvedThreadStatus,
                    ct);

            LogThreadResolved(logger, threadId, job.PullRequestId);
        }
        else if (canReply && !resolution.IsResolved && resolution.ReplyText is not null && hasNewReplies)
        {
            // Posted because a person spoke, never because of where the revision happens to be. A thread nobody
            // replied to, judged unresolved on a new revision, stays silent: nobody asked it anything.
            repliedThread = CreateReviewThreadRef(job, thread, threadId);
            repliedCommentId = await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                .ReplyAsync(job.ClientId, repliedThread, resolution.ReplyText, ct);
        }

        // Provenance last, after the status update the reply announces. It is bookkeeping, and nothing that
        // can throw may sit between a comment saying the thread is closed and the call that closes it: a
        // cancellation in that gap leaves the closing note standing on a thread that is still open.
        if (repliedThread is not null)
        {
            await this.RecordPostedReplyOriginAsync(job, repliedThread.ExternalThreadId, repliedCommentId, ct);
        }
    }

    private async Task<ThreadPassRuntime?> TryResolveRuntimeAsync(ThreadPassJob job, CancellationToken ct)
    {
        if (aiRuntimeResolver is null)
        {
            return null;
        }

        var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ReviewDefault, ct);

        // Stored on the row so what the pass spent can later be priced against the connection the tokens were
        // bought through, the same way a review job records its own.
        await threadPassJobs.SetAiConfigAsync(
            job.Id,
            runtime.Connection.Id,
            runtime.Model.RemoteModelId,
            ct);

        return new ThreadPassRuntime(
            runtime.ChatClient,
            runtime.Model.RemoteModelId,
            runtime.LogicalModelName,
            runtime.Model.MaxContextTokens,
            runtime.Model.TokenizerName);
    }

    /// <summary>
    ///     Opens the trace record for one thread's evaluation. Returns <see langword="null" /> when the record
    ///     cannot be opened, which costs the trace but never the answer the developer is waiting for.
    /// </summary>
    private async Task<Guid?> BeginThreadProtocolAsync(
        ThreadPassJob job,
        string threadId,
        string evaluationKind,
        ThreadPassRuntime runtime,
        CancellationToken ct)
    {
        try
        {
            return await protocolRecorder.BeginForThreadPassAsync(
                job.Id,
                job.AttemptCount,
                $"thread-{threadId}-{evaluationKind}",
                runtime.ModelId,
                ct,
                runtime.LogicalModelName);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogThreadProtocolBeginFailed(logger, job.Id, threadId, ex);
            return null;
        }
    }

    private async Task RecordEvaluationProtocolAsync(
        Guid? protocolId,
        ThreadResolutionResult resolution,
        CancellationToken ct)
    {
        if (protocolId is not { } id)
        {
            return;
        }

        // An evaluation that had to request more code spent more than one call. Each is recorded separately
        // so a trace can distinguish them, and the totals below are the sum, because the sum is what the
        // client is billed.
        var calls = resolution.Calls is { Count: > 0 } spentCalls
            ? spentCalls
            :
            [
                new ThreadResolutionCall(
                    resolution.InputTokens,
                    resolution.OutputTokens,
                    resolution.CachedInputTokens,
                    resolution.CacheWriteTokens,
                    resolution.ReasoningTokens),
            ];

        // The tokens are already spent and the developer is waiting on an answer. Accounting that fails costs a
        // number; refusing to answer because of it costs the work.
        try
        {
            for (var index = 0; index < calls.Count; index++)
            {
                var call = calls[index];
                await protocolRecorder.RecordAiCallAsync(
                    id,
                    index + 1,
                    call.InputTokens,
                    call.OutputTokens,
                    null,
                    null,

                    // The reply belongs to the call that produced it, which is the last one.
                    index == calls.Count - 1 ? resolution.ReplyText : null,
                    ct,
                    cachedInputTokens: call.CachedInputTokens,
                    cacheWriteTokens: call.CacheWriteTokens,
                    reasoningTokens: call.ReasoningTokens);
            }

            await protocolRecorder.SetCompletedAsync(
                id,
                resolution.IsResolved ? "Resolved" : "NotResolved",
                resolution.InputTokens ?? 0,
                resolution.OutputTokens ?? 0,
                calls.Count,
                0,
                null,
                ct,
                resolution.CachedInputTokens ?? 0,
                resolution.CachedInputTokens.HasValue
                    ? CacheObservabilityStatus.Observable
                    : CacheObservabilityStatus.Unobservable,
                resolution.CacheWriteTokens ?? 0,
                resolution.ReasoningTokens ?? 0);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogThreadSpendRecordingFailed(logger, id, ex);
        }
    }

    /// <summary>
    ///     Builds the pass's ownership answer: this pull request's provenance, read once, plus the identity
    ///     the provider authenticated the fetch as.
    /// </summary>
    /// <remarks>
    ///     A degraded answer still decides which threads to handle, because identity alone is enough for the
    ///     common case. It is not enough to decide which stored thread rows to retire: a thread the degraded
    ///     answer fails to recognise is not a thread that has gone away.
    /// </remarks>
    private async Task<ThreadPassOwnership> ResolveThreadOwnershipAsync(
        ThreadPassJob job,
        PullRequest pullRequest,
        CancellationToken ct)
    {
        var identity = new ThreadOwnerIdentity(pullRequest.AuthorizedIdentityId, pullRequest.AuthorizedIdentityName);
        var commentIdScope = ProviderCommentIdScopes.For(job.Provider);

        if (postedCommentOriginStore is null)
        {
            return new ThreadPassOwnership(ThreadOwnershipResolver.Create([], identity, commentIdScope), false);
        }

        try
        {
            var provenance = await postedCommentOriginStore.GetJobIdsForPullRequestAsync(
                job.ClientId,
                job.RepositoryId,
                job.PullRequestId,
                ct);
            return new ThreadPassOwnership(
                ThreadOwnershipResolver.Create(provenance, identity, commentIdScope),
                true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogThreadOwnershipProvenanceLookupFailed(logger, job.Id, ex);
            return new ThreadPassOwnership(ThreadOwnershipResolver.Create([], identity, commentIdScope), false);
        }
    }

    private async Task RecordPostedReplyOriginAsync(
        ThreadPassJob job,
        string providerThreadId,
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
                        providerThreadId,
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

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(
        PullRequest pullRequest,
        ThreadOwnershipResolver ownership)
    {
        if (pullRequest.ExistingThreads is null)
        {
            return [];
        }

        // A thread the provider does not name cannot be answered, resolved, or recorded as handled, because
        // every one of those needs an identifier the provider will accept back. Excluding it here keeps the
        // pass from spending a model call on an answer it could never publish.
        return pullRequest.ExistingThreads
            .Where(thread => thread.Comments.Count > 0
                             && !string.IsNullOrWhiteSpace(thread.ThreadId)
                             && ownership.OwnsThread(ToCommentRef(thread, thread.Comments[0])))
            .ToList()
            .AsReadOnly();
    }

    // Counted on the same terms the trigger counts on: the provider's own activity entries are not somebody
    // replying. Counting them here and not there made the pass store a number the trigger could never reach,
    // which silenced the reply arm of the trigger for good on any thread carrying a system entry.
    private static int CountNonReviewerComments(PrCommentThread thread, ThreadOwnershipResolver ownership)
    {
        return thread.Comments.Count(comment =>
            !comment.IsSystemGenerated && !ownership.OwnsComment(ToCommentRef(thread, comment)));
    }

    // Azure DevOps scopes a comment id to its thread, so both ids travel together: the pair is what
    // provenance was recorded under, and on every other provider the comment id resolves on its own.
    private static ThreadCommentRef ToCommentRef(PrCommentThread thread, PrThreadComment comment)
    {
        return new ThreadCommentRef(
            thread.ThreadId,
            comment.CommentId.ToString(CultureInfo.InvariantCulture),
            comment.AuthorId,
            comment.AuthorName);
    }

    private static ReviewThreadRef CreateReviewThreadRef(ThreadPassJob job, PrCommentThread thread, string threadId)
    {
        return new ReviewThreadRef(
            job.CodeReviewReference,
            threadId,
            thread.FilePath,
            thread.LineNumber,
            true);
    }

    /// <summary>
    ///     Names what one thread's evaluation was given, for the trace record. All three are one model call; the
    ///     name says which inputs it carried, so a trace shows whether a reply was in front of the model.
    /// </summary>
    private static string DescribeEvaluation(bool revisionMoved, bool hasNewReplies)
    {
        if (!revisionMoved)
        {
            return "conversational";
        }

        return hasNewReplies ? "code-change-with-reply" : "code-change";
    }

    private static bool IsResolvedStatus(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Decides what actually happens to a thread the model has judged: whether its status changes, and
    ///     whether anything is said.
    /// </summary>
    /// <remarks>
    ///     Two different questions decide the reply, and conflating them is what made a developer who fixed a
    ///     finding and asked why in the same breath get silence. Narrating a resolution is the client's choice,
    ///     expressed by <see cref="CommentResolutionBehavior.WithReply" />. Answering a person who asked
    ///     something is not: they are owed an answer whatever the client thinks about closing notes, which is
    ///     the rule the open-thread path already follows and the mention path has always followed.
    /// </remarks>
    private static ResolvedThreadAction BuildResolvedThreadAction(
        string threadId,
        PrCommentThread thread,
        CommentResolutionBehavior behavior,
        ThreadResolutionResult resolution,
        bool canReply,
        bool hasNewReplies)
    {
        var normalizedReplyText = string.IsNullOrWhiteSpace(resolution.ReplyText)
            ? null
            : resolution.ReplyText.Trim();

        if (!resolution.IsResolved)
        {
            return new ResolvedThreadAction(
                threadId,
                behavior,
                false,
                normalizedReplyText,
                false,
                false,
                ResolvedThreadReasonSource.None);
        }

        var canSaySomething = canReply && normalizedReplyText is not null;

        if (behavior == CommentResolutionBehavior.WithReply)
        {
            // A client that asked to be told why a thread closed is not served by closing it silently, so
            // being unable to say anything withholds the resolution too. That is long-standing behaviour.
            var shouldReplyAndResolve = canSaySomething;
            return new ResolvedThreadAction(
                threadId,
                behavior,
                shouldReplyAndResolve,
                normalizedReplyText,
                shouldReplyAndResolve,
                shouldReplyAndResolve,
                shouldReplyAndResolve ? ResolvedThreadReasonSource.AiGenerated : ResolvedThreadReasonSource.None);
        }

        // Silent resolution narrates nothing, which is the point of it. A question is not narration: someone
        // asked, and the thread happening to resolve in the same pass does not unask it.
        return new ResolvedThreadAction(
            threadId,
            behavior,
            true,
            normalizedReplyText,
            canSaySomething && hasNewReplies,
            true,
            normalizedReplyText is not null
                ? ResolvedThreadReasonSource.AiGenerated
                : ResolvedThreadReasonSource.None);
    }

    /// <summary>The chat runtime one pass judges its threads with, resolved once for the whole pass.</summary>
    /// <param name="ChatClient">The client-scoped chat client the evaluations run through.</param>
    /// <param name="ModelId">The provider's own identifier for the deployed model.</param>
    /// <param name="LogicalModelName">The name the model is configured under, for pricing and traces.</param>
    /// <param name="MaxContextTokens">
    ///     The model's context window, which determines how much code one evaluation may retrieve. A larger
    ///     window permits more, so the limit follows the model the client configured rather than a fixed
    ///     number.
    /// </param>
    /// <param name="TokenizerName">The model's tokenizer, so what fits is measured rather than estimated.</param>
    private sealed record ThreadPassRuntime(
        IChatClient ChatClient,
        string ModelId,
        string? LogicalModelName,
        int? MaxContextTokens,
        string? TokenizerName);

    /// <summary>
    ///     The pass's ownership answer, and whether every input it wanted was available.
    /// </summary>
    /// <param name="Resolver">Decides which threads are ProPR's.</param>
    /// <param name="IsComplete">
    ///     False when the posting provenance could not be read, so an unrecognised thread may still be one of
    ///     ProPR's. Deleting stored rows on that answer would take the thread-memory state machine's record of
    ///     a resolution with it.
    /// </param>
    private sealed record ThreadPassOwnership(ThreadOwnershipResolver Resolver, bool IsComplete);

    [LoggerMessage(
        EventId = 6401,
        Level = LogLevel.Debug,
        Message = "Thread pass {ThreadPassJobId} was already claimed by another executor.")]
    private static partial void LogPassAlreadyClaimed(ILogger logger, Guid threadPassJobId);

    [LoggerMessage(
        EventId = 6402,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} for PR {PullRequestId} failed; attempts remaining: {AttemptsRemain}.")]
    private static partial void LogPassAttemptFailed(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        bool attemptsRemain,
        Exception exception);

    [LoggerMessage(
        EventId = 6403,
        Level = LogLevel.Information,
        Message = "Thread pass {ThreadPassJobId} skipped: PR {PullRequestId} is {PullRequestStatus}.")]
    private static partial void LogPassSkippedForInactivePullRequest(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        string pullRequestStatus);

    [LoggerMessage(
        EventId = 6404,
        Level = LogLevel.Information,
        Message = "Thread pass {ThreadPassJobId} handled {ThreadCount} reviewer thread(s) on PR {PullRequestId}.")]
    private static partial void LogPassCompleted(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        int threadCount);

    [LoggerMessage(
        EventId = 6405,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} left threads unhandled on PR {PullRequestId}; attempts remaining: {AttemptsRemain}.")]
    private static partial void LogPassPartiallyHandled(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        bool attemptsRemain);

    [LoggerMessage(
        EventId = 6406,
        Level = LogLevel.Warning,
        Message = "Evaluating thread {ThreadId} on PR {PullRequestId} failed.")]
    private static partial void LogThreadHandlingFailed(
        ILogger logger,
        string threadId,
        int pullRequestId,
        Exception exception);

    [LoggerMessage(
        EventId = 6407,
        Level = LogLevel.Information,
        Message = "Resolved thread {ThreadId} on PR {PullRequestId}.")]
    private static partial void LogThreadResolved(ILogger logger, string threadId, int pullRequestId);

    [LoggerMessage(
        EventId = 6408,
        Level = LogLevel.Warning,
        Message = "Comment-origin lookup failed for thread pass {ThreadPassJobId}; ownership falls back to identity alone.")]
    private static partial void LogThreadOwnershipProvenanceLookupFailed(
        ILogger logger,
        Guid threadPassJobId,
        Exception exception);

    [LoggerMessage(
        EventId = 6409,
        Level = LogLevel.Warning,
        Message = "Recording posted-comment provenance for thread pass {ThreadPassJobId} failed.")]
    private static partial void LogPostedCommentOriginRecordingFailed(
        ILogger logger,
        Guid threadPassJobId,
        Exception exception);

    [LoggerMessage(
        EventId = 6410,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} held: the {BudgetScope} budget has spent {SpentUsd} of {ThresholdUsd} USD.")]
    private static partial void LogPassHeldByBudget(
        ILogger logger,
        Guid threadPassJobId,
        BudgetScopeKind budgetScope,
        decimal thresholdUsd,
        decimal spentUsd);

    [LoggerMessage(
        EventId = 6411,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} stopped: the {BudgetScope} budget has spent {SpentUsd} of {ThresholdUsd} USD.")]
    private static partial void LogPassCutByBudget(
        ILogger logger,
        Guid threadPassJobId,
        BudgetScopeKind budgetScope,
        decimal thresholdUsd,
        decimal spentUsd);

    [LoggerMessage(
        EventId = 6412,
        Level = LogLevel.Warning,
        Message = "Opening the trace record for thread {ThreadId} on thread pass {ThreadPassJobId} failed.")]
    private static partial void LogThreadProtocolBeginFailed(
        ILogger logger,
        Guid threadPassJobId,
        string threadId,
        Exception exception);

    [LoggerMessage(
        EventId = 6413,
        Level = LogLevel.Warning,
        Message = "Recording what trace record {ProtocolId} spent failed; the tokens are spent but uncounted.")]
    private static partial void LogThreadSpendRecordingFailed(ILogger logger, Guid protocolId, Exception exception);

    [LoggerMessage(
        EventId = 6414,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} could not read the threads on PR {PullRequestId}; nothing advanced. Attempts remaining: {AttemptsRemain}.")]
    private static partial void LogThreadsUnreadable(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        bool attemptsRemain);

    [LoggerMessage(
        EventId = 6415,
        Level = LogLevel.Information,
        Message =
            "Thread pass {ThreadPassJobId} skipped: PR {PullRequestId} moved past iteration {IterationId} before the pass ran.")]
    private static partial void LogPassSkippedForMovedRevision(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        int iterationId);

    [LoggerMessage(
        EventId = 6416,
        Level = LogLevel.Information,
        Message = "Thread pass {ThreadPassJobId} stopped part-way: PR {PullRequestId} cancelled it while it ran.")]
    private static partial void LogPassAbandonedAfterCancellation(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId);

    [LoggerMessage(
        EventId = 6417,
        Level = LogLevel.Warning,
        Message = "Thread pass {ThreadPassJobId} could not read the latest revision; it runs at the one it was queued at.")]
    private static partial void LogLatestRevisionLookupFailed(
        ILogger logger,
        Guid threadPassJobId,
        Exception exception);

    [LoggerMessage(
        EventId = 6418,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} could not fetch the file thread {ThreadId} is anchored to; it is judged without the diff.")]
    private static partial void LogThreadFileLookupFailed(
        ILogger logger,
        Guid threadPassJobId,
        string threadId,
        Exception exception);

    [LoggerMessage(
        EventId = 6419,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} could not fetch {FilePath}, requested by a thread evaluation; the thread is evaluated without it.")]
    private static partial void LogRequestedFileLookupFailed(
        ILogger logger,
        Guid threadPassJobId,
        string filePath,
        Exception exception);

    [LoggerMessage(
        EventId = 6420,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} refused {FilePath}: a thread evaluation requested a file this pull request did not change.")]
    private static partial void LogRequestedFileOutsidePullRequest(
        ILogger logger,
        Guid threadPassJobId,
        string filePath);

    [LoggerMessage(
        EventId = 6421,
        Level = LogLevel.Warning,
        Message =
            "Thread pass {ThreadPassJobId} could not list the files PR {PullRequestId} changed; its threads are evaluated without cross-file evidence.")]
    private static partial void LogChangedFileManifestUnavailable(
        ILogger logger,
        Guid threadPassJobId,
        int pullRequestId,
        Exception exception);
}
