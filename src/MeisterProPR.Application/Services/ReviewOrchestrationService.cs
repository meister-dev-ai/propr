// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
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
    IReviewJobExecutionStore jobs,
    IPullRequestFetcher prFetcher,
    IFileByFileReviewOrchestrator fileByFileOrchestrator,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAdoThreadClient threadClient,
    IAdoThreadReplier threadReplier,
    IAiCommentResolutionCore resolutionCore,
    IReviewProtocolRecorder protocolRecorder,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger,
    IAiConnectionRepository aiConnectionRepository,
    IAiChatClientFactory aiChatClientFactory,
    IPromptOverrideService? promptOverrideService = null) : IReviewJobProcessor
{
    private readonly AiReviewOptions _opts = options.Value;

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewerId = await this.ResolveReviewerIdAsync(job, ct);
        if (reviewerId is null)
        {
            return;
        }

        var overrideChatClient = await this.ResolveAiConnectionAsync(job, ct);
        if (overrideChatClient is null)
        {
            return;
        }

        PullRequest? pr = null;

        try
        {
            pr = await this.RunReviewPipelineAsync(job, reviewerId.Value, overrideChatClient, ct);
        }
        catch (PartialReviewFailureException ex)
        {
            await this.HandlePartialReviewFailureAsync(job, pr, ex, ct);
            return;
        }
        catch (Exception ex)
        {
            LogReviewFailed(logger, job.Id, ex);
            await jobs.SetFailedAsync(job.Id, ex.Message, ct);
            return;
        }

        if (pr is not null)
        {
            await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId.Value), reviewerId.Value, pr.AuthorizedIdentityId, ct);
        }
    }

    private async Task<PullRequest?> RunReviewPipelineAsync(
        ReviewJob job, Guid reviewerId, IChatClient overrideChatClient, CancellationToken ct)
    {
        LogReviewStarted(logger, job.Id, job.PullRequestId);

        var (scan, isNewIteration, compareToIterationId) = await this.LoadScanStateAsync(job, ct);

        var pr = await this.FetchPullRequestAsync(job, compareToIterationId, ct);
        if (pr is null)
        {
            return null;
        }

        var reviewerThreads = GetReviewerThreads(pr, reviewerId);

        if (!isNewIteration && !HasNewThreadReplies(reviewerThreads, scan!, reviewerId, pr.AuthorizedIdentityId))
        {
            LogSkippedNoChange(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        await reviewerManager.AddOptionalReviewerAsync(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.PullRequestId, reviewerId, job.ClientId, ct);

        await this.EvaluateExistingThreadsAsync(job, pr, reviewerThreads, scan, isNewIteration, reviewerId, overrideChatClient, ct);

        if (!isNewIteration)
        {
            LogSkippedNoChange(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        var (systemContext, carriedForwardPaths) = await this.BuildReviewContextAsync(
            job, pr, compareToIterationId, overrideChatClient, ct);

        if (systemContext is null)
        {
            LogSkippedNoChange(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        if (jobs.GetById(job.Id)?.Status == JobStatus.Cancelled)
        {
            LogJobCancelledBeforeFileReview(logger, job.Id);
            return null;
        }

        var result = await this.DispatchFileReviewAsync(job, pr, systemContext, overrideChatClient, ct);

        if (jobs.GetById(job.Id)?.Status == JobStatus.Cancelled)
        {
            LogJobCancelledAfterFileReview(logger, job.Id);
            return null;
        }

        if (carriedForwardPaths.Count > 0)
        {
            result = result with { CarriedForwardFilePaths = carriedForwardPaths };
        }

        if (string.IsNullOrWhiteSpace(result.Summary) && result.Comments.Count == 0)
        {
            LogSkippedEmptyReview(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        await this.PublishReviewResultAsync(job, pr, result, ct);
        return pr;
    }

    private async Task SaveScanAndDeleteJobAsync(ReviewJob job, PullRequest pr, Guid reviewerId, CancellationToken ct)
    {
        await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId), reviewerId, pr.AuthorizedIdentityId, ct);
        await jobs.DeleteAsync(job.Id, ct);
    }

    private async Task PublishReviewResultAsync(ReviewJob job, PullRequest pr, ReviewResult result, CancellationToken ct)
    {
        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, "posting", null, ct: ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, job.Id, ex);
        }

        try
        {
            var diagnostics = await commentPoster.PostAsync(
                job.OrganizationUrl, job.ProjectId, job.RepositoryId,
                job.PullRequestId, job.IterationId, result, job.ClientId,
                pr.ExistingThreads, ct);

            await jobs.SetResultAsync(job.Id, result, ct);

            if (protocolId.HasValue)
            {
                await this.RecordPostingDiagnosticsAsync(protocolId.Value, diagnostics, ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Completed", 0, 0, 0, 0, null, ct);
            }

            LogReviewCompleted(logger, job.Id);
        }
        catch
        {
            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_operation_failed",
                    JsonSerializer.Serialize(new
                    {
                        operation = "publish_review_result",
                        jobId = job.Id,
                        pullRequestId = job.PullRequestId,
                        iterationId = job.IterationId,
                        repositoryId = job.RepositoryId,
                        clientId = job.ClientId,
                    }),
                    "Failed while posting the review result.",
                    ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }

            throw;
        }
    }

    // T069: Resolve reviewer identity — returns null when not configured (caller sets job failed).
    private async Task<Guid?> ResolveReviewerIdAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId, ct);
        if (reviewerId is null)
        {
            LogReviewerIdentityMissing(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(job.Id, $"Reviewer identity not configured for client {job.ClientId}", ct);
        }

        return reviewerId;
    }

    // T070: Resolve per-client AI connection — returns null when not configured (caller sets job failed).
    private async Task<IChatClient?> ResolveAiConnectionAsync(ReviewJob job, CancellationToken ct)
    {
        var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(job.ClientId, ct);
        if (activeConnection is null)
        {
            LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(job.Id, $"No active AI connection configured for client {job.ClientId}. Configure one via the admin UI.", ct);
            return null;
        }

        var effectiveModelId = activeConnection.ActiveModel ?? activeConnection.Models.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            await jobs.SetFailedAsync(
                job.Id,
                $"Active AI connection for client {job.ClientId} has no model deployment selected. Activate a deployment in the admin UI.",
                ct);
            return null;
        }

        var client = aiChatClientFactory.CreateClient(activeConnection.EndpointUrl, activeConnection.ApiKey);
        job.SetAiConfig(activeConnection.Id, effectiveModelId);
        await jobs.UpdateAiConfigAsync(job.Id, activeConnection.Id, effectiveModelId, ct);
        return client;
    }

    // T071: Load scan state — returns (existingScan, isNewIteration, compareToIterationId).
    private async Task<(ReviewPrScan? scan, bool isNewIteration, int? compareToIterationId)> LoadScanStateAsync(
        ReviewJob job, CancellationToken ct)
    {
        var scan = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
        var iterationKey = job.IterationId.ToString();
        var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

        int? compareToIterationId = null;
        if (isNewIteration && scan is not null && int.TryParse(scan.LastProcessedCommitId, out var prevIterationId))
        {
            compareToIterationId = prevIterationId;
        }

        return (scan, isNewIteration, compareToIterationId);
    }

    // T072: Fetch PR and guard the active status — returns null if PR is no longer active (job already updated).
    private async Task<PullRequest?> FetchPullRequestAsync(ReviewJob job, int? compareToIterationId, CancellationToken ct)
    {
        var pr = await prFetcher.FetchAsync(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.PullRequestId, job.IterationId, compareToIterationId, job.ClientId, ct);

        if (pr.Status == PrStatus.Active)
        {
            return pr;
        }

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

        return null;
    }

    // T073: Evaluate existing reviewer threads if any are present.
    private async Task EvaluateExistingThreadsAsync(
        ReviewJob job, PullRequest pr, IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan, bool isNewIteration, Guid reviewerId, IChatClient chatClient, CancellationToken ct)
    {
        if (reviewerThreads.Count == 0)
        {
            return;
        }

        var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
        await this.EvaluateReviewerThreadsAsync(job, pr, reviewerThreads, scan, isNewIteration, behavior, reviewerId, chatClient, ct);
    }

    // T074: Build review context — carry-forward prior results, fetch instructions and exclusions.
    // Returns (systemContext, carriedForwardPaths); systemContext is null when all files were carried
    // forward with an empty delta (no AI review needed — caller should save scan and delete job).
    private async Task<(ReviewSystemContext? systemContext, List<string> carriedForwardPaths)> BuildReviewContextAsync(
        ReviewJob job, PullRequest pr, int? compareToIterationId, IChatClient chatClient, CancellationToken ct)
    {
        var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();
        var carriedForwardPaths = new List<string>();

        if (compareToIterationId.HasValue)
        {
            var priorJob = await jobs.GetCompletedJobWithFileResultsAsync(
                job.OrganizationUrl, job.ProjectId, job.RepositoryId,
                job.PullRequestId, compareToIterationId.Value, ct);

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

                if (changedFilePaths.Count == 0 && carriedForwardPaths.Count > 0)
                {
                    return (null, carriedForwardPaths);
                }
            }
        }

        var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);
        var reviewTools = reviewContextToolsFactory.Create(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            pr.SourceBranch,
            job.PullRequestId,
            job.IterationId,
            job.ClientId,
            job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                ? job.ProCursorSourceIds
                : null);

        var fetchedInstructions = await instructionFetcher.FetchAsync(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId, pr.TargetBranch, job.ClientId, ct);
        var relevantInstructions = fetchedInstructions.Count > 0
            ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
            : (IReadOnlyList<RepositoryInstruction>)[];

        // IRepositoryExclusionFetcher.FetchAsync is contractually non-throwing and returns
        // defaults on failure. The defensive catch below is belt-and-suspenders.
        ReviewExclusionRules exclusionRules;
        try
        {
            exclusionRules = await exclusionFetcher.FetchAsync(
                job.OrganizationUrl, job.ProjectId, job.RepositoryId, pr.TargetBranch, job.ClientId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch exclusion rules for job {JobId}; using defaults", job.Id);
            exclusionRules = ReviewExclusionRules.Default;
        }

        var systemContext = new ReviewSystemContext(customSystemMessage, relevantInstructions, reviewTools)
        {
            ExclusionRules = exclusionRules,
            ModelId = job.AiModel,
            PromptOverrides = await LoadPromptOverridesAsync(job.ClientId, promptOverrideService, logger, ct),
        };

        return (systemContext, carriedForwardPaths);
    }

    // T075: Dispatch the file-by-file review and merge carry-forward paths into the result.
    private async Task<ReviewResult> DispatchFileReviewAsync(
        ReviewJob job, PullRequest pr, ReviewSystemContext systemContext, IChatClient chatClient, CancellationToken ct)
    {
        return await fileByFileOrchestrator.ReviewAsync(job, pr, systemContext, ct, chatClient);
    }

    private async Task HandlePartialReviewFailureAsync(
        ReviewJob job, PullRequest? pr, PartialReviewFailureException ex, CancellationToken ct)
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
                    await this.PublishReviewResultAsync(
                        job,
                        pr ?? new PullRequest(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.RepositoryId,
                            job.PullRequestId,
                            job.IterationId,
                            string.Empty,
                            null,
                            string.Empty,
                            string.Empty,
                            [],
                            ExistingThreads: pr?.ExistingThreads),
                        partial,
                        ct);
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
    }

    private async Task RecordPostingDiagnosticsAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        var summaryDetails = JsonSerializer.Serialize(new
        {
            candidateCount = diagnostics.CandidateCount,
            postedCount = diagnostics.PostedCount,
            suppressedCount = diagnostics.SuppressedCount,
            suppressionReasons = diagnostics.SuppressionReasons,
            consideredOpenThreads = diagnostics.ConsideredOpenThreads,
            consideredResolvedThreads = diagnostics.ConsideredResolvedThreads,
            usedFallbackChecks = diagnostics.UsedFallbackChecks,
            carriedForwardCandidatesSkipped = diagnostics.CarriedForwardCandidatesSkipped,
        });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_summary", summaryDetails, null, ct);

        if (!diagnostics.IsDegraded)
        {
            return;
        }

        var degradedModeDetails = JsonSerializer.Serialize(new
        {
            cause = diagnostics.DegradedCause ?? "Duplicate protection ran in degraded mode.",
            degradedComponents = diagnostics.DegradedComponents,
            fallbackChecks = diagnostics.FallbackChecks,
            affectedCandidateCount = diagnostics.AffectedCandidateCount,
            reviewContinued = true,
        });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_degraded_mode", degradedModeDetails, null, ct);
    }

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(PullRequest pr, Guid reviewerId)
    {
        if (pr.ExistingThreads is null)
        {
            return [];
        }

        return pr.ExistingThreads
            .Where(t => IsReviewerOwnedAuthor(t.Comments.FirstOrDefault()?.AuthorId, reviewerId, pr.AuthorizedIdentityId))
            .ToList()
            .AsReadOnly();
    }

    private static bool HasNewThreadReplies(
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan scan,
        Guid reviewerId,
        Guid? authorizedIdentityId)
    {
        foreach (var thread in reviewerThreads)
        {
            var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
            var storedCount = stored?.LastSeenReplyCount ?? 0;
            var userReplyCount = CountNonReviewerComments(thread.Comments, reviewerId, authorizedIdentityId);
            if (userReplyCount > storedCount)
            {
                return true;
            }
        }

        return false;
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
        IChatClient chatClient,
        CancellationToken ct)
    {
        if (behavior == CommentResolutionBehavior.Disabled)
        {
            return;
        }

        foreach (var thread in reviewerThreads)
        {
            var stored = scan?.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);

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

                var storedCount = stored?.LastSeenReplyCount ?? 0;
                var userReplyCount = CountNonReviewerComments(thread.Comments, reviewerId, pr.AuthorizedIdentityId);
                var hasNewReplies = userReplyCount > storedCount;

                var evaluationKind = isNewIteration ? "code-change" : "conversational";
                Guid? protocolId = null;
                try
                {
                    protocolId = await protocolRecorder.BeginAsync(
                        job.Id, job.RetryCount + 1,
                        $"thread-{thread.ThreadId}-{evaluationKind}",
                        null, ct: ct);
                }
                catch (Exception ex)
                {
                    LogProtocolBeginFailed(logger, job.Id, ex);
                }

                if (isNewIteration)
                {
                    resolution = await resolutionCore.EvaluateCodeChangeAsync(thread, pr, chatClient, job.AiModel ?? this._opts.ModelId, ct);
                }
                else if (hasNewReplies)
                {
                    resolution = await resolutionCore.EvaluateConversationalReplyAsync(thread, chatClient, job.AiModel ?? this._opts.ModelId, ct);
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

    private async Task SaveScanAsync(
        ReviewJob job,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        Guid reviewerId,
        Guid? authorizedIdentityId,
        CancellationToken ct)
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
                        LastSeenReplyCount = CountNonReviewerComments(thread.Comments, reviewerId, authorizedIdentityId),
                        LastSeenStatus = thread.Status,
                    });
            }

            await prScanRepository.UpsertAsync(scan, ct);
        }
        catch (Exception ex)
        {
            LogScanSaveFailed(logger, job.Id, ex);
        }
    }

    private static bool IsResolvedStatus(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReviewerOwnedAuthor(Guid? authorId, Guid reviewerId, Guid? authorizedIdentityId)
    {
        return authorId.HasValue &&
               (authorId.Value == reviewerId ||
                authorizedIdentityId.HasValue && authorId.Value == authorizedIdentityId.Value);
    }

    private static int CountNonReviewerComments(
        IReadOnlyList<PrThreadComment> comments,
        Guid reviewerId,
        Guid? authorizedIdentityId)
    {
        return comments.Count(comment => !IsReviewerOwnedAuthor(comment.AuthorId, reviewerId, authorizedIdentityId));
    }

    private static string BuildCommentHistory(PrCommentThread thread)
    {
        if (thread.Comments.Count == 0)
        {
            return "(no comments)";
        }

        return string.Join("\n", thread.Comments.Select(c => $"{c.AuthorName}: {c.Content}"));
    }
}
