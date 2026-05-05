// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Support;
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
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAiCommentResolutionCore resolutionCore,
    IProtocolRecorder protocolRecorder,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger,
    IAiConnectionRepository aiConnectionRepository,
    IAiChatClientFactory aiChatClientFactory,
    IPromptOverrideService? promptOverrideService = null,
    IProviderActivationService? providerActivationService = null,
    IAiRuntimeResolver? aiRuntimeResolver = null) : IReviewJobProcessor
{
    private readonly AiReviewOptions _opts = options.Value;

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        if (providerActivationService is not null && !await providerActivationService.IsEnabledAsync(job.Provider, ct))
        {
            await jobs.SetFailedAsync(
                job.Id,
                "The provider family is currently disabled by system administration.",
                ct);
            return;
        }

        var reviewerContext = await this.ResolveReviewerAsync(job, ct);
        if (reviewerContext is null)
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
            pr = await this.RunReviewPipelineAsync(
                job,
                reviewerContext.Value.reviewerId,
                reviewerContext.Value.reviewer,
                overrideChatClient,
                ct);
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
            await this.SaveScanAsync(
                job,
                GetReviewerThreads(pr, reviewerContext.Value.reviewerId),
                reviewerContext.Value.reviewerId,
                pr.AuthorizedIdentityId,
                ct);
        }
    }

    private async Task<PullRequest?> RunReviewPipelineAsync(
        ReviewJob job,
        Guid reviewerId,
        ReviewerIdentity reviewer,
        IChatClient overrideChatClient,
        CancellationToken ct)
    {
        LogReviewStarted(logger, job.Id, job.PullRequestId);

        var (scan, isNewIteration, compareToIterationId) = await this.LoadScanStateAsync(job, ct);

        var pr = await this.FetchPullRequestAsync(job, compareToIterationId, ct);
        if (pr is null)
        {
            return null;
        }

        var reviewerThreads = GetReviewerThreads(pr, reviewerId);
        var providerCapabilities = providerRegistry.GetRegisteredCapabilities(job.Provider) ?? [];

        if (!isNewIteration && !HasNewThreadReplies(reviewerThreads, scan!, reviewerId, pr.AuthorizedIdentityId))
        {
            LogSkippedNoChange(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        if (providerCapabilities.Any(capability => string.Equals(
                capability,
                "reviewAssignment",
                StringComparison.Ordinal)))
        {
            await providerRegistry.GetReviewAssignmentService(job.Provider)
                .AddOptionalReviewerAsync(job.ClientId, job.CodeReviewReference, reviewer, ct);
        }

        await this.EvaluateExistingThreadsAsync(
            job,
            pr,
            reviewerThreads,
            scan,
            isNewIteration,
            reviewerId,
            providerCapabilities,
            overrideChatClient,
            ct);

        if (!isNewIteration)
        {
            LogSkippedNoChange(logger, job.Id, job.PullRequestId);
            await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
            return null;
        }

        var (systemContext, carriedForwardPaths) = await this.BuildReviewContextAsync(
            job,
            pr,
            compareToIterationId,
            overrideChatClient,
            ct);

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

        await this.PublishReviewResultAsync(job, pr, result, reviewer, ct);
        return pr;
    }

    private async Task SaveScanAndDeleteJobAsync(ReviewJob job, PullRequest pr, Guid reviewerId, CancellationToken ct)
    {
        await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId), reviewerId, pr.AuthorizedIdentityId, ct);
        await jobs.DeleteAsync(job.Id, ct);
    }

    private async Task PublishReviewResultAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct)
    {
        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, "posting", ct: ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, job.Id, ex);
        }

        try
        {
            var publicationResult = this.PrepareResultForPublication(job, pr, result);
            var scmCommentPostingEnabled = await clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, ct);
            var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty(
                publicationResult.Comments.Count + publicationResult.CarriedForwardCandidatesSkipped,
                publicationResult.CarriedForwardCandidatesSkipped);

            if (scmCommentPostingEnabled)
            {
                var publicationRevision = await this.ResolvePublicationReviewRevisionAsync(job, ct);
                diagnostics = await providerRegistry.GetCodeReviewPublicationService(job.Provider)
                    .PublishReviewAsync(
                        job.ClientId,
                        job.CodeReviewReference,
                        publicationRevision,
                        publicationResult,
                        reviewer,
                        ct);
            }

            await jobs.SetResultAsync(job.Id, publicationResult, ct);

            if (protocolId.HasValue)
            {
                await this.RecordPostingDiagnosticsAsync(protocolId.Value, diagnostics, ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Completed", 0, 0, 0, 0, null, ct);
            }

            LogReviewCompleted(logger, job.Id);
        }
        catch (Exception ex)
        {
            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_operation_failed",
                    JsonSerializer.Serialize(
                        new
                        {
                            operation = "publish_review_result",
                            jobId = job.Id,
                            pullRequestId = job.PullRequestId,
                            iterationId = job.IterationId,
                            repositoryId = job.RepositoryId,
                            clientId = job.ClientId,
                            errorType = ex.GetType().FullName,
                            errorMessage = ex.Message,
                        }),
                    $"Failed while posting the review result: {ex.Message}",
                    ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }

            throw;
        }
    }

    // Resolve the normalized reviewer identity plus the current internal GUID needed by the scan model.
    private async Task<(ReviewerIdentity reviewer, Guid reviewerId)?> ResolveReviewerAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        var normalizedReviewer = await clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, ct);
        if (normalizedReviewer is not null)
        {
            var normalizedReviewerId = Guid.TryParse(normalizedReviewer.ExternalUserId, out var parsedReviewerId)
                ? parsedReviewerId
                : StableGuidGenerator.Create(normalizedReviewer.ExternalUserId);
            return (normalizedReviewer, normalizedReviewerId);
        }

        LogReviewerIdentityMissing(logger, job.ClientId, job.Id);
        await jobs.SetFailedAsync(job.Id, $"Reviewer identity not configured for client {job.ClientId}", ct);
        return null;
    }

    // T070: Resolve per-client AI connection — returns null when not configured (caller sets job failed).
    private async Task<IChatClient?> ResolveAiConnectionAsync(ReviewJob job, CancellationToken ct)
    {
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ReviewDefault, ct);
                job.SetAiConfig(runtime.Connection.Id, runtime.Model.RemoteModelId, job.ReviewTemperature);
                await jobs.UpdateAiConfigAsync(job.Id, runtime.Connection.Id, runtime.Model.RemoteModelId, ct, job.ReviewTemperature);
                return runtime.ChatClient;
            }
            catch (Exception ex)
            {
                LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
                await jobs.SetFailedAsync(job.Id, ex.Message, ct);
                return null;
            }
        }

        var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(job.ClientId, ct);
        if (activeConnection is null)
        {
            LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(
                job.Id,
                $"No active AI connection configured for client {job.ClientId}. Configure one via the admin UI.",
                ct);
            return null;
        }

        var effectiveModelId = activeConnection.GetBoundModelId(AiPurpose.ReviewDefault)
                               ?? activeConnection.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            await jobs.SetFailedAsync(
                job.Id,
                $"Active AI connection for client {job.ClientId} has no model deployment selected. Activate a deployment in the admin UI.",
                ct);
            return null;
        }

        var client = aiChatClientFactory.CreateClient(activeConnection.BaseUrl, activeConnection.Secret);
        job.SetAiConfig(activeConnection.Id, effectiveModelId, job.ReviewTemperature);
        await jobs.UpdateAiConfigAsync(job.Id, activeConnection.Id, effectiveModelId, ct, job.ReviewTemperature);
        return client;
    }

    // T071: Load scan state — returns (existingScan, isNewIteration, compareToIterationId).
    private async Task<(ReviewPrScan? scan, bool isNewIteration, int? compareToIterationId)> LoadScanStateAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        var scan = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
        var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
        var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

        int? compareToIterationId = null;
        if (isNewIteration && scan is not null)
        {
            compareToIterationId = ReviewRevisionKeys.TryParseIterationId(scan.LastProcessedCommitId);
        }

        return (scan, isNewIteration, compareToIterationId);
    }

    // T072: Fetch PR and guard the active status — returns null if PR is no longer active (job already updated).
    private async Task<PullRequest?> FetchPullRequestAsync(
        ReviewJob job,
        int? compareToIterationId,
        CancellationToken ct)
    {
        var pr = await prFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            compareToIterationId,
            job.ClientId,
            ct);

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
        ReviewJob job,
        PullRequest pr,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan,
        bool isNewIteration,
        Guid reviewerId,
        IReadOnlyList<string> providerCapabilities,
        IChatClient chatClient,
        CancellationToken ct)
    {
        if (reviewerThreads.Count == 0)
        {
            return;
        }

        if (!providerCapabilities.Any(capability => string.Equals(
                capability,
                "reviewThreadStatus",
                StringComparison.Ordinal)))
        {
            return;
        }

        var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
        var canReply = providerCapabilities.Any(capability => string.Equals(
            capability,
            "reviewThreadReply",
            StringComparison.Ordinal));
        await this.EvaluateReviewerThreadsAsync(
            job,
            pr,
            reviewerThreads,
            scan,
            isNewIteration,
            behavior,
            reviewerId,
            canReply,
            chatClient,
            ct);
    }

    // T074: Build review context — carry-forward prior results, fetch instructions and exclusions.
    // Returns (systemContext, carriedForwardPaths); systemContext is null when all files were carried
    // forward with an empty delta (no AI review needed — caller should save scan and delete job).
    private async Task<(ReviewSystemContext? systemContext, List<string> carriedForwardPaths)> BuildReviewContextAsync(
        ReviewJob job,
        PullRequest pr,
        int? compareToIterationId,
        IChatClient chatClient,
        CancellationToken ct)
    {
        var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();
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

                if (changedFilePaths.Count == 0 && carriedForwardPaths.Count > 0)
                {
                    return (null, carriedForwardPaths);
                }
            }
        }

        var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);

        var reviewTools = reviewContextToolsFactory.Create(
            new ReviewContextToolsRequest(
                job.CodeReviewReference,
                pr.SourceBranch,
                job.IterationId,
                job.ClientId,
                job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                    ? job.ProCursorSourceIds
                    : null,
                job.OrganizationUrl));
        IReadOnlyList<RepositoryInstruction> relevantInstructions = [];
        var exclusionRules = ReviewExclusionRules.Default;

        var fetchedInstructions = await instructionFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            pr.TargetBranch,
            job.ClientId,
            ct);
        relevantInstructions = fetchedInstructions.Count > 0
            ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
            : [];

        // IRepositoryExclusionFetcher.FetchAsync is contractually non-throwing and returns
        // defaults on failure. The defensive catch below is belt-and-suspenders.
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
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = job.AiModel,
            ExclusionRules = exclusionRules,
            ModelId = job.AiModel,
            Temperature = job.ReviewTemperature,
            PromptOverrides = await LoadPromptOverridesAsync(job.ClientId, promptOverrideService, logger, ct),
        };

        return (systemContext, carriedForwardPaths);
    }

    // T075: Dispatch the file-by-file review and merge carry-forward paths into the result.
    private async Task<ReviewResult> DispatchFileReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext systemContext,
        IChatClient chatClient,
        CancellationToken ct)
    {
        return await fileByFileOrchestrator.ReviewAsync(job, pr, systemContext, ct, chatClient);
    }

    private async Task HandlePartialReviewFailureAsync(
        ReviewJob job,
        PullRequest? pr,
        PartialReviewFailureException ex,
        CancellationToken ct)
    {
        LogPartialReviewFailure(logger, job.Id, ex.FailedCount, ex.TotalCount);

        job.RetryCount++;
        await jobs.UpdateRetryCountAsync(job.Id, job.RetryCount, ct);

        if (job.RetryCount >= this._opts.MaxFileReviewRetries)
        {
            // On the final retry, post any partial results from the files that succeeded
            // rather than silently discarding them.
            if (ex.PartialResult is { } partial &&
                (!string.IsNullOrWhiteSpace(partial.Summary) || partial.Comments.Count > 0))
            {
                var reviewerContext = await this.ResolveReviewerAsync(job, ct);
                if (reviewerContext is null)
                {
                    return;
                }

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
                        reviewerContext.Value.reviewer,
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
        var summaryDetails = JsonSerializer.Serialize(
            new
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

        var degradedModeDetails = JsonSerializer.Serialize(
            new
            {
                cause = diagnostics.DegradedCause ?? "Duplicate protection ran in degraded mode.",
                degradedComponents = diagnostics.DegradedComponents,
                fallbackChecks = diagnostics.FallbackChecks,
                affectedCandidateCount = diagnostics.AffectedCandidateCount,
                reviewContinued = true,
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_degraded_mode", degradedModeDetails, null, ct);
    }

    private ReviewResult PrepareResultForPublication(ReviewJob job, PullRequest pr, ReviewResult result)
    {
        if (!RequiresInsertedInlineAnchors(job.Provider) || result.Comments.Count == 0)
        {
            return result;
        }

        var insertedLinesByPath = BuildInsertedLineLookup(pr.ChangedFiles);
        var normalizedComments = new List<ReviewComment>(result.Comments.Count);
        var downgradedCount = 0;

        foreach (var comment in result.Comments)
        {
            if (!CanUseGitLabInlineAnchor(comment, insertedLinesByPath))
            {
                if (!string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                    comment.LineNumber.Value > 0)
                {
                    downgradedCount++;
                    normalizedComments.Add(
                        new ReviewComment(
                            null,
                            null,
                            comment.Severity,
                            $"{NormalizeReviewPath(comment.FilePath)}:L{comment.LineNumber.Value}: {comment.Message}"));
                    continue;
                }
            }

            normalizedComments.Add(comment);
        }

        if (downgradedCount == 0)
        {
            return result;
        }

        logger.LogInformation(
            "Downgraded {DowngradedCount} {Provider} inline review comment(s) to overview comments for job {JobId} because the referenced lines were not inserted diff lines.",
            downgradedCount,
            job.Provider,
            job.Id);

        return result with { Comments = normalizedComments.AsReadOnly() };
    }

    private static bool RequiresInsertedInlineAnchors(ScmProvider provider)
    {
        return provider is ScmProvider.GitLab or ScmProvider.Forgejo;
    }

    private static bool CanUseGitLabInlineAnchor(
        ReviewComment comment,
        IReadOnlyDictionary<string, HashSet<int>> insertedLinesByPath)
    {
        if (string.IsNullOrWhiteSpace(comment.FilePath) || !comment.LineNumber.HasValue || comment.LineNumber.Value < 1)
        {
            return true;
        }

        return insertedLinesByPath.TryGetValue(NormalizeReviewPath(comment.FilePath), out var insertedLines)
               && insertedLines.Contains(comment.LineNumber.Value);
    }

    private static IReadOnlyDictionary<string, HashSet<int>> BuildInsertedLineLookup(IReadOnlyList<ChangedFile> changedFiles)
    {
        var lookup = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            lookup[NormalizeReviewPath(changedFile.Path)] = ExtractInsertedNewLines(changedFile);
        }

        return lookup;
    }

    private static HashSet<int> ExtractInsertedNewLines(ChangedFile changedFile)
    {
        var insertedLines = new HashSet<int>();
        var diffLines = changedFile.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;

        foreach (var diffLine in diffLines)
        {
            if (diffLine.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
                {
                    currentNewLine = newLineStart;
                    hasHunkHeader = true;
                }

                continue;
            }

            if (!hasHunkHeader)
            {
                continue;
            }

            if (diffLine.StartsWith("+++", StringComparison.Ordinal) ||
                diffLine.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (diffLine.StartsWith("+", StringComparison.Ordinal))
            {
                insertedLines.Add(currentNewLine);
                currentNewLine++;
                continue;
            }

            if (diffLine.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (diffLine.StartsWith(" ", StringComparison.Ordinal))
            {
                currentNewLine++;
            }
        }

        if (!hasHunkHeader && changedFile.ChangeType == ChangeType.Add)
        {
            var lineCount = CountLines(changedFile.FullContent);
            for (var lineNumber = 1; lineNumber <= lineCount; lineNumber++)
            {
                insertedLines.Add(lineNumber);
            }
        }

        return insertedLines;
    }

    private static bool TryParseUnifiedDiffNewLineStart(string diffLine, out int newLineStart)
    {
        newLineStart = 0;

        var plusIndex = diffLine.IndexOf('+');
        if (plusIndex < 0)
        {
            return false;
        }

        var endIndex = plusIndex + 1;
        while (endIndex < diffLine.Length && char.IsDigit(diffLine[endIndex]))
        {
            endIndex++;
        }

        return endIndex > plusIndex + 1
               && int.TryParse(diffLine[(plusIndex + 1)..endIndex], out newLineStart)
               && newLineStart > 0;
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
    }

    private static string NormalizeReviewPath(string path)
    {
        return path.TrimStart('/');
    }

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(PullRequest pr, Guid reviewerId)
    {
        if (pr.ExistingThreads is null)
        {
            return [];
        }

        return pr.ExistingThreads
            .Where(t => IsReviewerOwnedAuthor(
                t.Comments.FirstOrDefault()?.AuthorId,
                reviewerId,
                pr.AuthorizedIdentityId))
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
            foreach (var key in PromptOverride.ValidPromptKeys)
            {
                var text = await service.GetOverrideAsync(clientId, null, key, ct);
                if (text is not null)
                {
                    overrides[key] = text;
                }
            }

            return overrides;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load prompt overrides for client {ClientId}; review will proceed with global defaults",
                clientId);
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
        bool canReply,
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
                        job.Id,
                        job.RetryCount + 1,
                        $"thread-{thread.ThreadId}-{evaluationKind}",
                        ct: ct);
                }
                catch (Exception ex)
                {
                    LogProtocolBeginFailed(logger, job.Id, ex);
                }

                if (isNewIteration)
                {
                    resolution = await resolutionCore.EvaluateCodeChangeAsync(
                        thread,
                        pr,
                        chatClient,
                        job.AiModel ?? this._opts.ModelId,
                        ct);
                }
                else if (hasNewReplies)
                {
                    resolution = await resolutionCore.EvaluateConversationalReplyAsync(
                        thread,
                        chatClient,
                        job.AiModel ?? this._opts.ModelId,
                        ct);
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
                        protocolId.Value,
                        1,
                        resolution.InputTokens,
                        resolution.OutputTokens,
                        null,
                        null,
                        resolution.ReplyText,
                        ct);
                    var outcome = resolution.IsResolved ? "Resolved" : "NotResolved";
                    await protocolRecorder.SetCompletedAsync(
                        protocolId.Value,
                        outcome,
                        resolution.InputTokens ?? 0,
                        resolution.OutputTokens ?? 0,
                        1,
                        0,
                        null,
                        ct);
                }

                if (resolution.IsResolved)
                {
                    if (canReply && behavior == CommentResolutionBehavior.WithReply && resolution.ReplyText is not null)
                    {
                        await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                            .ReplyAsync(job.ClientId, CreateReviewThreadRef(job, thread), resolution.ReplyText, ct);
                    }

                    await providerRegistry.GetReviewThreadStatusWriter(job.Provider)
                        .UpdateThreadStatusAsync(job.ClientId, CreateReviewThreadRef(job, thread), "fixed", ct);

                    LogThreadResolved(logger, thread.ThreadId, job.PullRequestId);
                }
                else if (canReply && !resolution.IsResolved && resolution.ReplyText is not null && !isNewIteration)
                {
                    await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                        .ReplyAsync(job.ClientId, CreateReviewThreadRef(job, thread), resolution.ReplyText, ct);
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
            var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
            var scan = new ReviewPrScan(scanId, job.ClientId, job.RepositoryId, job.PullRequestId, iterationKey);

            foreach (var thread in reviewerThreads)
            {
                scan.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = scanId,
                        ThreadId = thread.ThreadId,
                        LastSeenReplyCount =
                            CountNonReviewerComments(thread.Comments, reviewerId, authorizedIdentityId),
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
                (authorizedIdentityId.HasValue && authorId.Value == authorizedIdentityId.Value));
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

    private static ReviewThreadRef CreateReviewThreadRef(ReviewJob job, PrCommentThread thread)
    {
        return new ReviewThreadRef(
            job.CodeReviewReference,
            thread.ThreadId.ToString(CultureInfo.InvariantCulture),
            thread.FilePath,
            thread.LineNumber,
            true);
    }

    private async Task<ReviewRevision> ResolvePublicationReviewRevisionAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewRevision = job.ReviewRevisionReference;
        if (job.Provider != ScmProvider.AzureDevOps && RequiresLiveRevisionRefresh(reviewRevision))
        {
            var latestRevision = await providerRegistry
                .GetCodeReviewQueryService(job.Provider)
                .GetLatestRevisionAsync(job.ClientId, job.CodeReviewReference, ct);

            if (latestRevision is not null)
            {
                logger.LogInformation(
                    "Refreshed invalid or missing review revision before publication for job {JobId} and provider {Provider}.",
                    job.Id,
                    job.Provider);
                return latestRevision;
            }
        }

        return ResolveReviewRevision(job);
    }

    private static bool RequiresLiveRevisionRefresh(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return true;
        }

        return !LooksLikeCommitSha(revision.HeadSha)
               || !LooksLikeCommitSha(revision.BaseSha)
               || (revision.StartSha is not null && !LooksLikeCommitSha(revision.StartSha));
    }

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ReviewRevision ResolveReviewRevision(ReviewJob job)
    {
        if (job.ReviewRevisionReference is { } reviewRevision)
        {
            return reviewRevision;
        }

        if (job.Provider == ScmProvider.AzureDevOps)
        {
            var legacyRevisionId = job.IterationId.ToString(CultureInfo.InvariantCulture);
            return new ReviewRevision(
                $"ado-head-{legacyRevisionId}",
                $"ado-base-{legacyRevisionId}",
                null,
                legacyRevisionId,
                null);
        }

        throw new InvalidOperationException($"Review job {job.Id} is missing normalized review revision data for provider {job.Provider}.");
    }
}
