using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job.
/// </summary>
public sealed partial class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IFileByFileReviewOrchestrator fileByFileOrchestrator,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAdoThreadClient threadClient,
    IAdoThreadReplier threadReplier,
    IAiCommentResolutionCore resolutionCore,
    IProtocolRecorder protocolRecorder,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiChatClientFactory = null,
    IFindingDismissalRepository? dismissalRepository = null,
    IPromptOverrideService? promptOverrideService = null)
{
    private readonly AiReviewOptions _opts = options.Value;

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId, ct);
        if (reviewerId is null)
        {
            LogReviewerIdentityMissing(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(job.Id, $"Reviewer identity not configured for client {job.ClientId}", ct);
            return;
        }

        // Resolve per-client AI connection if available; fall back to global singleton.
        IChatClient? overrideChatClient = null;
        if (aiConnectionRepository is not null && aiChatClientFactory is not null)
        {
            var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(job.ClientId, ct);
            if (activeConnection is not null)
            {
                overrideChatClient = aiChatClientFactory.CreateClient(
                    activeConnection.EndpointUrl,
                    null); // ApiKey is intentionally not surfaced in DTO; use DefaultAzureCredential
                job.SetAiConfig(activeConnection.Id, activeConnection.ActiveModel);
                await jobs.UpdateAiConfigAsync(job.Id, activeConnection.Id, activeConnection.ActiveModel, ct);
            }
        }

        PullRequest? pr = null;

        try
        {
            LogReviewStarted(logger, job.Id, job.PullRequestId);

            // Fetch the scan first so we know whether this is a re-review and can request
            // only the delta files from the fetcher (files changed since the last reviewed iteration).
            var scan = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
            var iterationKey = job.IterationId.ToString();
            var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

            int? compareToIterationId = null;
            if (isNewIteration && scan is not null && int.TryParse(scan.LastProcessedCommitId, out var prevIterationId))
            {
                compareToIterationId = prevIterationId;
            }

            pr = await prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                compareToIterationId,
                job.ClientId,
                ct);

            if (pr.Status != PrStatus.Active)
            {
                LogPrNoLongerActive(logger, job.PullRequestId, pr.Status, job.Id);
                if (pr.Status == PrStatus.Abandoned)
                {
                    LogPrAbandonedCancellingJob(logger, job.PullRequestId, job.Id);
                    await jobs.SetCancelledAsync(job.Id, ct);
                }
                else
                {
                    await jobs.SetFailedAsync(job.Id, "PR was closed or abandoned before review could begin", ct);
                }

                return;
            }

            var reviewerThreads = GetReviewerThreads(pr, reviewerId.Value);

            if (!isNewIteration && !HasNewThreadReplies(reviewerThreads, scan!, reviewerId.Value))
            {
                LogSkippedNoChange(logger, job.Id, job.PullRequestId);
                await this.SaveScanAsync(job, reviewerThreads, reviewerId.Value, ct);
                await jobs.DeleteAsync(job.Id, ct);
                return;
            }

            await reviewerManager.AddOptionalReviewerAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                reviewerId.Value,
                job.ClientId,
                ct);

            if (reviewerThreads.Count > 0)
            {
                var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
                await this.EvaluateReviewerThreadsAsync(job, pr, reviewerThreads, scan, isNewIteration, behavior, reviewerId.Value, ct);
            }

            // No new commit was pushed — conversational thread evaluation above is all that's needed.
            // Skip the full file-by-file review entirely.
            if (!isNewIteration)
            {
                LogSkippedNoChange(logger, job.Id, job.PullRequestId);
                await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId.Value), reviewerId.Value, ct);
                await jobs.DeleteAsync(job.Id, ct);
                return;
            }

            var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);
            var reviewTools = reviewContextToolsFactory.Create(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pr.SourceBranch,
                job.PullRequestId,
                job.IterationId,
                job.ClientId);

            // Fetch repository-level instructions from the TARGET branch only (prevents prompt injection)
            var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();

            // Carry-forward: when this is a new iteration following a previously-reviewed one,
            // look up the prior iteration's completed file results. Files that have not changed
            // are inherited without dispatching a new AI call.
            var carriedForwardPaths = new List<string>();
            if (compareToIterationId.HasValue)
            {
                var priorJob = await jobs.GetCompletedJobWithFileResultsAsync(
                    job.OrganizationUrl,
                    job.ProjectId,
                    job.RepositoryId,
                    job.PullRequestId,
                    compareToIterationId.Value,
                    ct);

                if (priorJob is not null)
                {
                    var changedPathsSet = new HashSet<string>(changedFilePaths, StringComparer.OrdinalIgnoreCase);
                    foreach (var priorResult in priorJob.FileReviewResults
                                 .Where(fr => fr.IsComplete && !fr.IsFailed && !fr.IsExcluded && !fr.IsCarriedForward))
                    {
                        if (!changedPathsSet.Contains(priorResult.FilePath))
                        {
                            var carried = ReviewFileResult.CreateCarriedForward(job.Id, priorResult);
                            await jobs.AddFileResultAsync(carried, ct);
                            carriedForwardPaths.Add(priorResult.FilePath);
                        }
                    }

                    // If all files were carried forward and the delta is empty, no new review is needed.
                    if (changedFilePaths.Count == 0 && carriedForwardPaths.Count > 0)
                    {
                        LogSkippedNoChange(logger, job.Id, job.PullRequestId);
                        await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId.Value), reviewerId.Value, ct);
                        await jobs.DeleteAsync(job.Id, ct);
                        return;
                    }
                }
            }

            var fetchedInstructions = await instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pr.TargetBranch,
                job.ClientId,
                ct);
            var relevantInstructions = fetchedInstructions.Count > 0
                ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
                : (IReadOnlyList<RepositoryInstruction>)[];

            // IRepositoryExclusionFetcher.FetchAsync is contractually non-throwing and returns
            // defaults on failure. The defensive catch below is belt-and-suspenders in case a
            // future implementation does not honour the contract.
            ReviewExclusionRules exclusionRules;
            try
            {
                exclusionRules = await exclusionFetcher.FetchAsync(
                    job.OrganizationUrl,
                    job.ProjectId,
                    job.RepositoryId,
                    pr.TargetBranch,
                    job.ClientId,
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch exclusion rules for job {JobId}; using defaults", job.Id);
                exclusionRules = ReviewExclusionRules.Default;
            }

            var systemContext = new ReviewSystemContext(customSystemMessage, relevantInstructions, reviewTools)
            {
                ExclusionRules = exclusionRules,
                DismissedPatterns = await LoadDismissedPatternsAsync(job.ClientId, dismissalRepository, logger, ct),
                PromptOverrides = await LoadPromptOverridesAsync(job.ClientId, promptOverrideService, logger, ct),
            };

            // Checkpoint B: another worker may have cancelled this job while we were setting up.
            // Re-read the current job status and abort without AI calls if it is now Cancelled.
            if (jobs.GetById(job.Id)?.Status == JobStatus.Cancelled)
            {
                LogJobCancelledBeforeFileReview(logger, job.Id);
                return;
            }

            var result = await fileByFileOrchestrator.ReviewAsync(job, pr, systemContext, ct, overrideChatClient);

            // Checkpoint C: job may have been cancelled while file review was in progress.
            // Discard the result and return without posting a comment.
            if (jobs.GetById(job.Id)?.Status == JobStatus.Cancelled)
            {
                LogJobCancelledAfterFileReview(logger, job.Id);
                return;
            }

            // Merge carried-forward file paths into the result so the comment poster can include them.
            if (carriedForwardPaths.Count > 0)
            {
                result = result with { CarriedForwardFilePaths = carriedForwardPaths };
            }

            if (string.IsNullOrWhiteSpace(result.Summary) && result.Comments.Count == 0)
            {
                LogSkippedEmptyReview(logger, job.Id, job.PullRequestId);
                await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId.Value), reviewerId.Value, ct);
                await jobs.DeleteAsync(job.Id, ct);
                return;
            }

            await commentPoster.PostAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                result,
                job.ClientId,
                pr.ExistingThreads,
                ct);

            await jobs.SetResultAsync(job.Id, result, ct);

            LogReviewCompleted(logger, job.Id);
        }
        catch (PartialReviewFailureException ex)
        {
            LogPartialReviewFailure(logger, job.Id, ex.FailedCount, ex.TotalCount);

            job.RetryCount++;
            await jobs.UpdateRetryCountAsync(job.Id, job.RetryCount, ct);

            if (job.RetryCount >= this._opts.MaxFileReviewRetries)
            {
                // On the final retry, post any partial results from the files that succeeded
                // rather than silently discarding them.
                if (ex.PartialResult is { } partial && (!string.IsNullOrWhiteSpace(partial.Summary) || partial.Comments.Count > 0))
                {
                    try
                    {
                        await commentPoster.PostAsync(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.PullRequestId,
                            job.IterationId,
                            partial,
                            job.ClientId,
                            pr?.ExistingThreads,
                            ct);
                        await jobs.SetResultAsync(job.Id, partial, ct);
                        LogReviewCompleted(logger, job.Id);
                        return;
                    }
                    catch (Exception postEx)
                    {
                        LogReviewFailed(logger, job.Id, postEx);
                    }
                }

                await jobs.SetFailedAsync(job.Id, $"Max retries reached. {ex.Message}", ct);
            }
            else
            {
                // Re-queue the job so the worker picks it up again without waiting for a restart.
                // FileByFileReviewOrchestrator skips already-completed file results on the next pass.
                await jobs.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, ct);
            }

            return;
        }
        catch (Exception ex)
        {
            LogReviewFailed(logger, job.Id, ex);
            await jobs.SetFailedAsync(job.Id, ex.Message, ct);
            return;
        }

        await this.SaveScanAsync(job, GetReviewerThreads(pr!, reviewerId.Value), reviewerId.Value, ct);
    }

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(PullRequest pr, Guid reviewerId)
    {
        if (pr.ExistingThreads is null)
        {
            return [];
        }

        return pr.ExistingThreads
            .Where(t => t.Comments.FirstOrDefault()?.AuthorId == reviewerId)
            .ToList()
            .AsReadOnly();
    }

    private static bool HasNewThreadReplies(IReadOnlyList<PrCommentThread> reviewerThreads, ReviewPrScan scan, Guid reviewerId)
    {
        foreach (var thread in reviewerThreads)
        {
            var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
            var storedCount = stored?.LastSeenReplyCount ?? 0;
            var userReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId);
            if (userReplyCount > storedCount)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Loads dismissed patterns for the given client.
    ///     Returns an empty list on null repository, cancellation, or any exception (graceful degradation).
    /// </summary>
    private static async Task<IReadOnlyList<string>> LoadDismissedPatternsAsync(
        Guid clientId,
        IFindingDismissalRepository? repo,
        ILogger logger,
        CancellationToken ct)
    {
        if (repo is null)
        {
            return [];
        }

        try
        {
            var dismissals = await repo.GetByClientAsync(clientId, ct);
            return dismissals.Select(d => d.PatternText).ToList().AsReadOnly();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load dismissed patterns for client {ClientId}; review will proceed without exclusions", clientId);
            return [];
        }
    }

    /// <summary>
    ///     Loads prompt overrides for every known prompt key for the given client.
    ///     Returns an empty dictionary on null service, cancellation, or any exception (graceful degradation).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> LoadPromptOverridesAsync(
        Guid clientId,
        IPromptOverrideService? service,
        ILogger logger,
        CancellationToken ct)
    {
        if (service is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in MeisterProPR.Domain.Entities.PromptOverride.ValidPromptKeys)
            {
                var text = await service.GetOverrideAsync(clientId, crawlConfigId: null, key, ct);
                if (text is not null)
                {
                    overrides[key] = text;
                }
            }

            return overrides;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load prompt overrides for client {ClientId}; review will proceed with global defaults", clientId);
            return new Dictionary<string, string>();
        }
    }

    private async Task EvaluateReviewerThreadsAsync(
        ReviewJob job,
        PullRequest pr,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan,
        bool isNewIteration,
        CommentResolutionBehavior behavior,
        Guid reviewerId,
        CancellationToken ct)
    {
        if (behavior == CommentResolutionBehavior.Disabled)
        {
            return;
        }

        foreach (var thread in reviewerThreads)
        {
            // Skip threads that ADO already reports as resolved — no AI call needed.
            if (string.Equals(thread.Status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "ByDesign", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                ThreadResolutionResult resolution;

                var stored = scan?.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
                var storedCount = stored?.LastSeenReplyCount ?? 0;
                var userReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId);
                var hasNewReplies = userReplyCount > storedCount;

                var evaluationKind = isNewIteration ? "code-change" : "conversational";
                Guid? protocolId = null;
                try
                {
                    protocolId = await protocolRecorder.BeginAsync(
                        job.Id, job.RetryCount + 1,
                        $"thread-{thread.ThreadId}-{evaluationKind}",
                        null, ct);
                }
                catch (Exception ex)
                {
                    LogProtocolBeginFailed(logger, job.Id, ex);
                }

                if (isNewIteration)
                {
                    resolution = await resolutionCore.EvaluateCodeChangeAsync(thread, pr, ct);
                }
                else if (hasNewReplies)
                {
                    resolution = await resolutionCore.EvaluateConversationalReplyAsync(thread, ct);
                }
                else
                {
                    if (protocolId.HasValue)
                    {
                        await protocolRecorder.SetCompletedAsync(protocolId.Value, "Skipped", 0, 0, 0, 0, null, ct);
                    }

                    continue;
                }

                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordAiCallAsync(
                        protocolId.Value, 1,
                        resolution.InputTokens, resolution.OutputTokens,
                        null, resolution.ReplyText,
                        ct);
                    var outcome = resolution.IsResolved ? "Resolved" : "NotResolved";
                    await protocolRecorder.SetCompletedAsync(
                        protocolId.Value, outcome,
                        resolution.InputTokens ?? 0, resolution.OutputTokens ?? 0,
                        1, 0, null, ct);
                }

                if (resolution.IsResolved)
                {
                    if (behavior == CommentResolutionBehavior.WithReply && resolution.ReplyText is not null)
                    {
                        await threadReplier.ReplyAsync(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.PullRequestId,
                            thread.ThreadId,
                            resolution.ReplyText,
                            job.ClientId,
                            ct);
                    }

                    await threadClient.UpdateThreadStatusAsync(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        job.PullRequestId,
                        thread.ThreadId,
                        "fixed",
                        job.ClientId,
                        ct);

                    LogThreadResolved(logger, thread.ThreadId, job.PullRequestId);
                }
                else if (!resolution.IsResolved && resolution.ReplyText is not null && !isNewIteration)
                {
                    await threadReplier.ReplyAsync(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        job.PullRequestId,
                        thread.ThreadId,
                        resolution.ReplyText,
                        job.ClientId,
                        ct);
                }
            }
            catch (Exception ex)
            {
                LogThreadEvaluationFailed(logger, thread.ThreadId, job.PullRequestId, ex);
            }
        }
    }

    private async Task SaveScanAsync(ReviewJob job, IReadOnlyList<PrCommentThread> reviewerThreads, Guid reviewerId, CancellationToken ct)
    {
        try
        {
            var existing = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
            var scanId = existing?.Id ?? Guid.NewGuid();
            var scan = new ReviewPrScan(scanId, job.ClientId, job.RepositoryId, job.PullRequestId, job.IterationId.ToString());

            foreach (var thread in reviewerThreads)
            {
                scan.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = scanId,
                        ThreadId = thread.ThreadId,
                        LastSeenReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId),
                    });
            }

            await prScanRepository.UpsertAsync(scan, ct);
        }
        catch (Exception ex)
        {
            LogScanSaveFailed(logger, job.Id, ex);
        }
    }
}
