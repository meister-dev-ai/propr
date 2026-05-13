// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI.FileByFileReview;

internal sealed partial class FileReviewer(
    IAiReviewCore aiCore,
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    AiReviewOptions options,
    ILogger<FileByFileReviewOrchestrator> logger,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IThreadMemoryService? memoryService,
    IAiRuntimeResolver? aiRuntimeResolver,
    CommentRelevanceFilterExecutor? commentRelevanceFilterExecutor,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    IReviewClaimExtractor? reviewClaimExtractor,
    IReviewFindingVerifier? reviewFindingVerifier)
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        ReviewFileResult? existingResult,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        LogFileReviewStarted(logger, file.Path, fileIndex, totalFiles, job.Id);

        var fileResult = await this.InitializeFileResultAsync(job, file, existingResult, ct);

        // Classify file complexity early so we can record tier in the protocol.
        var tier = FileByFileReviewOrchestrator.ClassifyTier(file);

        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };

        var tierPurpose = GetTierPurpose(tier);

        var (tierClient, tierModelId) = await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

        Guid? protocolId = await this.BeginNewProtocolAsync(job, file, fileResult, tierCategory, tierModelId, ct);

        ReviewSystemContext? fileContext = null;

        try
        {
            var relevantThreads = FilterThreadsForFile(pr.ExistingThreads, file.Path);
            var filePr = new PullRequest(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.RepositoryName,
                pr.PullRequestId,
                pr.IterationId,
                pr.Title,
                pr.Description,
                pr.SourceBranch,
                pr.TargetBranch,
                [file],
                pr.Status,
                relevantThreads);

            fileContext = this.CreateFileContext(
                job,
                pr,
                file,
                fileIndex,
                totalFiles,
                baseContext,
                protocolId,
                tier,
                tierModelId,
                tierClient,
                effectiveClient);

            var result = await this.ReviewFileCoreAsync(
                filePr,
                fileContext,
                ct);

            var pipelineState = new ReviewResultPipelineState(
                job,
                file,
                filePr,
                fileResult,
                fileContext,
                protocolId,
                reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? []);

            result = await this.RunReviewResultPipelineAsync(pipelineState, result, ct);

            await this.CompleteReviewAsync(fileResult, fileContext, protocolId, result, ct);

            LogFileReviewCompleted(logger, file.Path, job.Id);
        }
        catch (Exception ex)
        {
            await this.FailReviewAsync(fileResult, fileContext, protocolId, ex, ct);

            throw;
        }
    }

    private sealed record ReviewResultPipelineState(
        ReviewJob Job,
        ChangedFile File,
        PullRequest FilePullRequest,
        ReviewFileResult FileResult,
        ReviewSystemContext FileContext,
        Guid? ProtocolId,
        IReadOnlyList<InvariantFact> InvariantFacts);

    private async Task<ReviewFileResult> InitializeFileResultAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewFileResult? existingResult,
        CancellationToken ct)
    {
        if (existingResult is { IsComplete: false })
        {
            // Reuse the existing row — covers both jobs killed mid-flight (interrupted)
            // and previously failed rows (IsFailed=true, IsComplete=false).
            // Reset it so MarkCompleted / MarkFailed work correctly.
            existingResult.ResetForRetry();
            await jobRepository.UpdateFileResultAsync(existingResult, ct);
            return existingResult;
        }

        var fileResult = new ReviewFileResult(job.Id, file.Path);
        await jobRepository.AddFileResultAsync(fileResult, ct);
        return fileResult;
    }

    private ReviewSystemContext CreateFileContext(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        Guid? protocolId,
        FileComplexityTier tier,
        string? tierModelId,
        IChatClient? tierClient,
        IChatClient effectiveClient)
    {
        var changedLinesCount = FileByFileReviewOrchestrator.CountChangedLines(file.UnifiedDiff);
        LogTierAssigned(logger, file.Path, tier, changedLinesCount, job.Id);

        var maxIterationsOverride = tier switch
        {
            FileComplexityTier.Low => options.MaxIterationsLow,
            FileComplexityTier.Medium => options.MaxIterationsMedium,
            FileComplexityTier.High => options.MaxIterationsHigh,
            _ => options.MaxIterations,
        };

        if (maxIterationsOverride != options.MaxIterations)
        {
            LogMaxIterationsOverrideApplied(logger, maxIterationsOverride, file.Path, job.Id);
        }

        var fileContext = new ReviewSystemContext(
            baseContext.ClientSystemMessage,
            baseContext.RepositoryInstructions,
            baseContext.ReviewTools)
        {
            ActiveProtocolId = protocolId,
            DefaultReviewChatClient = baseContext.DefaultReviewChatClient,
            DefaultReviewModelId = baseContext.DefaultReviewModelId,
            ProtocolRecorder = protocolId.HasValue ? protocolRecorder : null,
            PerFileHint = new PerFileReviewHint(file.Path, fileIndex, totalFiles, pr.AllPrFileSummaries)
            {
                ComplexityTier = tier,
                MaxIterationsOverride = maxIterationsOverride,
            },
            ExclusionRules = baseContext.ExclusionRules,
            DismissedPatterns = baseContext.DismissedPatterns,
            ModelId = tierModelId ?? baseContext.ModelId,
            // Tier-specific client wins; fall back to per-client active connection so the
            // global default chatClient is never used when a per-client connection is configured.
            TierChatClient = tierClient ?? effectiveClient,
        };

        LogDismissalsInjected(logger, fileContext.DismissedPatterns?.Count ?? 0, file.Path, job.Id);
        return fileContext;
    }

    private async Task<ReviewResult> ReviewFileCoreAsync(
        PullRequest filePr,
        ReviewSystemContext fileContext,
        CancellationToken ct)
    {
        return await aiCore.ReviewAsync(filePr, fileContext, ct);
    }

    private async Task<ReviewResult> RunReviewResultPipelineAsync(
        ReviewResultPipelineState state,
        ReviewResult result,
        CancellationToken ct)
    {
        result = this.ApplyReviewFilters(state.Job, state.File, result, state.FileContext);
        result = await this.ApplyCommentRelevanceFilterStageAsync(state, result, ct);
        result = await this.ApplyMemoryReconsiderationStageAsync(state, result, ct);
        result = NormalizeCommentAnchors(result);
        return await this.ApplyLocalVerificationStageAsync(state, result, ct);
    }

    private async Task<ReviewResult> ApplyCommentRelevanceFilterStageAsync(
        ReviewResultPipelineState state,
        ReviewResult result,
        CancellationToken ct)
    {
        if (commentRelevanceFilterExecutor is null)
        {
            return result;
        }

        var request = new CommentRelevanceFilterRequest(
            state.Job.Id,
            state.FileResult.Id,
            null,
            state.File.Path,
            state.File,
            state.FilePullRequest,
            result.Comments,
            state.FileContext,
            state.ProtocolId);

        var filterResult = await commentRelevanceFilterExecutor.ExecuteAsync(request, ct);

        return filterResult is null
            ? result
            : result with { Comments = filterResult.GetKeptComments() };
    }

    private async Task<ReviewResult> ApplyMemoryReconsiderationStageAsync(
        ReviewResultPipelineState state,
        ReviewResult result,
        CancellationToken ct)
    {
        if (memoryService is null)
        {
            return result;
        }

        return await memoryService.RetrieveAndReconsiderAsync(
            state.Job.ClientId,
            state.Job,
            state.File.Path,
            state.File.UnifiedDiff,
            result,
            state.ProtocolId,
            ct,
            state.FileContext.Temperature);
    }

    private async Task<ReviewResult> ApplyLocalVerificationStageAsync(
        ReviewResultPipelineState state,
        ReviewResult result,
        CancellationToken ct)
    {
        return await this.ApplyLocalVerificationAsync(
            result,
            state.FileResult,
            state.ProtocolId,
            state.InvariantFacts,
            ct);
    }

    private ReviewResult ApplyReviewFilters(
        ReviewJob job,
        ChangedFile file,
        ReviewResult result,
        ReviewSystemContext fileContext)
    {
        var commentsBeforeConfidenceFloor = result.Comments;
        result = FileByFileReviewOrchestrator.ApplyConfidenceFloor(result, fileContext.LoopMetrics?.FinalConfidence, options);
        var confidenceDroppedCount = CountSeverityDowngrades(commentsBeforeConfidenceFloor, result.Comments);
        if (confidenceDroppedCount > 0)
        {
            LogSeverityDowngraded(logger, confidenceDroppedCount, file.Path, job.Id);
        }

        var beforeHedge = result.Comments.Count;
        result = FileByFileReviewOrchestrator.FilterSpeculativeComments(result);
        var hedgeDropped = beforeHedge - result.Comments.Count;
        if (hedgeDropped > 0)
        {
            LogSpeculativeCommentsDropped(logger, hedgeDropped, file.Path, job.Id);
        }

        var beforeInfo = result.Comments.Count;
        result = FileByFileReviewOrchestrator.StripInfoComments(result);
        var infoDropped = beforeInfo - result.Comments.Count;
        if (infoDropped > 0)
        {
            LogInfoCommentsDropped(logger, infoDropped, file.Path, job.Id);
        }

        var beforeVague = result.Comments.Count;
        result = FileByFileReviewOrchestrator.FilterVagueSuggestions(result);
        var vagueDropped = beforeVague - result.Comments.Count;
        if (vagueDropped > 0)
        {
            LogVagueSuggestionsDropped(logger, vagueDropped, file.Path, job.Id);
        }

        return result;
    }

    private async Task CompleteReviewAsync(
        ReviewFileResult fileResult,
        ReviewSystemContext fileContext,
        Guid? protocolId,
        ReviewResult result,
        CancellationToken ct)
    {
        fileResult.MarkCompleted(result.Summary, result.Comments);
        await jobRepository.UpdateFileResultAsync(fileResult, ct);

        if (protocolId.HasValue && fileContext.LoopMetrics is not null)
        {
            var m = fileContext.LoopMetrics;
            await protocolRecorder.SetCompletedAsync(
                protocolId.Value,
                "Completed",
                m.TotalInputTokens,
                m.TotalOutputTokens,
                m.Iterations,
                m.ToolCallCount,
                m.FinalConfidence,
                ct);
        }
    }

    private async Task FailReviewAsync(
        ReviewFileResult fileResult,
        ReviewSystemContext? fileContext,
        Guid? protocolId,
        Exception ex,
        CancellationToken ct)
    {
        fileResult.MarkFailed(ex.Message);
        await jobRepository.UpdateFileResultAsync(fileResult, ct);

        if (!protocolId.HasValue)
        {
            return;
        }

        var m = fileContext?.LoopMetrics;
        await protocolRecorder.RecordAiCallAsync(
            protocolId.Value,
            m?.Iterations ?? 1,
            0,
            0,
            null,
            null,
            null,
            ct,
            "ai_call_failure",
            ex.Message);

        await protocolRecorder.SetCompletedAsync(
            protocolId.Value,
            "Failed",
            m?.TotalInputTokens ?? 0,
            m?.TotalOutputTokens ?? 0,
            m?.Iterations ?? 0,
            m?.ToolCallCount ?? 0,
            null,
            ct);
    }

    private async Task<Guid?> BeginNewProtocolAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewFileResult fileResult,
        AiConnectionModelCategory tierCategory,
        string? tierModelId,
        CancellationToken ct)
    {
        try
        {
            return await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                file.Path,
                fileResult.Id,
                tierCategory,
                tierModelId,
                ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
            return null;
        }
    }

    private static AiPurpose GetTierPurpose(FileComplexityTier tier)
    {
        return tier switch
        {
            FileComplexityTier.Low => AiPurpose.ReviewLowEffort,
            FileComplexityTier.Medium => AiPurpose.ReviewMediumEffort,
            FileComplexityTier.High => AiPurpose.ReviewHighEffort,
            _ => AiPurpose.ReviewMediumEffort,
        };
    }

    private async Task<(IChatClient? tierClient, string? tierModelId)> ResolveTierClientAsync(
        ReviewJob job,
        AiConnectionModelCategory tierCategory,
        AiPurpose tierPurpose,
        CancellationToken ct)
    {
        IChatClient? tierClient = null;
        string? tierModelId = null;

        if (aiRuntimeResolver is not null)
        {
            try
            {
                var tierRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, tierPurpose, ct);
                tierClient = tierRuntime.ChatClient;
                tierModelId = tierRuntime.Model.RemoteModelId;
            }
            catch
            {
                tierClient = null;
                tierModelId = null;
            }
        }
        else if (aiConnectionRepository is not null && aiClientFactory is not null)
        {
            var tierDto = await aiConnectionRepository.GetForTierAsync(job.ClientId, tierCategory, ct);
            if (tierDto is not null)
            {
                tierClient = aiClientFactory.CreateClient(tierDto.BaseUrl, tierDto.Secret);
                tierModelId = tierDto.GetBoundModelId(tierPurpose)
                              ?? tierDto.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
            }
        }

        return (tierClient, tierModelId);
    }

    private async Task<ReviewResult> ApplyLocalVerificationAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct)
    {
        if (reviewClaimExtractor is null || reviewFindingVerifier is null || result.Comments.Count == 0)
        {
            return result;
        }

        var candidateFindings = result.Comments
            .Select((comment, index) => new CandidateReviewFinding(
                FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, index + 1),
                new CandidateFindingProvenance(
                    CandidateFindingProvenance.PerFileCommentOrigin,
                    "per_file_review",
                    fileResult.FilePath,
                    fileResult.Id,
                    index + 1),
                comment.Severity,
                comment.Message,
                FileByFileReviewOrchestrator.DetermineCategory(comment),
                comment.FilePath,
                FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)))
            .ToList();

        var claimsByFindingId = new Dictionary<string, IReadOnlyList<ClaimDescriptor>>(StringComparer.Ordinal);
        foreach (var finding in candidateFindings)
        {
            try
            {
                claimsByFindingId[finding.FindingId] = reviewClaimExtractor.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                claimsByFindingId[finding.FindingId] = [];
                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordVerificationEventAsync(
                        protocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                stage = ClaimDescriptor.LocalStage,
                                degradedComponent = "claim_extraction",
                            }),
                        null,
                        ex.Message,
                        ct);
                }
            }
        }

        var workItems = candidateFindings
            .SelectMany(finding => claimsByFindingId[finding.FindingId]
                .Select(claim => new VerificationWorkItem(
                    claim,
                    finding.Provenance,
                    claim.Stage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false)))
            .ToList();

        if (protocolId.HasValue)
        {
            foreach (var finding in candidateFindings)
            {
                var claims = claimsByFindingId[finding.FindingId];
                if (claims.Count == 0)
                {
                    continue;
                }

                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationClaimsExtracted,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = finding.FindingId,
                            filePath = finding.FilePath,
                            claimCount = claims.Count,
                        }),
                    JsonSerializer.Serialize(claims, FinalGateJsonOptions),
                    null,
                    ct);
            }
        }

        if (workItems.Count == 0)
        {
            return result;
        }

        var outcomes = await reviewFindingVerifier.VerifyAsync(workItems, invariantFacts, ct);
        var outcomesByFindingId = outcomes
            .GroupBy(outcome => outcome.FindingId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VerificationOutcome>)group.ToList(),
                StringComparer.Ordinal);

        if (protocolId.HasValue)
        {
            foreach (var outcome in outcomes)
            {
                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationLocalDecision,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = outcome.FindingId,
                            claimId = outcome.ClaimId,
                        }),
                    JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
                    null,
                    ct);
            }
        }

        var withheldFindingIds = outcomesByFindingId
            .Where(entry => !AreLocalOutcomesPublishable(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (withheldFindingIds.Count == 0)
        {
            return result;
        }

        var verifiedFindings = candidateFindings
            .Where(finding => !outcomesByFindingId.TryGetValue(finding.FindingId, out var findingOutcomes) || AreLocalOutcomesPublishable(findingOutcomes))
            .ToList();
        var verifiedComments = verifiedFindings
            .Select(finding => FileByFileReviewOrchestrator.CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
        var verifiedSummary = RewriteLocalVerificationSummary(candidateFindings, verifiedFindings, outcomesByFindingId);

        return result with
        {
            Summary = verifiedSummary,
            Comments = verifiedComments,
        };
    }

    private static bool AreLocalOutcomesPublishable(IReadOnlyList<VerificationOutcome> outcomes)
    {
        return outcomes.Count == 0 || outcomes.All(outcome =>
            string.Equals(outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
    }

    private static string RewriteLocalVerificationSummary(
        IReadOnlyList<CandidateReviewFinding> originalFindings,
        IReadOnlyList<CandidateReviewFinding> verifiedFindings,
        IReadOnlyDictionary<string, IReadOnlyList<VerificationOutcome>> outcomesByFindingId)
    {
        var summaryOnlyCount = 0;
        var dropCount = 0;

        foreach (var finding in originalFindings)
        {
            if (!outcomesByFindingId.TryGetValue(finding.FindingId, out var outcomes) || AreLocalOutcomesPublishable(outcomes))
            {
                continue;
            }

            if (outcomes.Any(outcome => string.Equals(outcome.RecommendedDisposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal)))
            {
                dropCount++;
                continue;
            }

            summaryOnlyCount++;
        }

        if (verifiedFindings.Count == 0)
        {
            var noFindingsBuilder = new StringBuilder("No actionable local findings remained after verification.");
            AppendLocalVerificationSuppressionSummary(noFindingsBuilder, summaryOnlyCount, dropCount);
            return noFindingsBuilder.ToString();
        }

        var builder = new StringBuilder();
        builder.Append($"Local verification retained {verifiedFindings.Count} actionable finding");
        builder.Append(verifiedFindings.Count == 1 ? "." : "s.");
        AppendLocalVerificationSuppressionSummary(builder, summaryOnlyCount, dropCount);

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Verified local findings:");

        foreach (var message in verifiedFindings
                     .Select(finding => finding.Message)
                     .Distinct(StringComparer.Ordinal)
                     .Take(5))
        {
            builder.Append("- ");
            builder.AppendLine(message);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendLocalVerificationSuppressionSummary(StringBuilder builder, int summaryOnlyCount, int dropCount)
    {
        if (summaryOnlyCount > 0)
        {
            builder.Append(' ');
            builder.Append(summaryOnlyCount);
            builder.Append(summaryOnlyCount == 1
                ? " candidate finding was withheld pending stronger evidence."
                : " candidate findings were withheld pending stronger evidence.");
        }

        if (dropCount > 0)
        {
            builder.Append(' ');
            builder.Append(dropCount);
            builder.Append(dropCount == 1
                ? " candidate finding was dropped by deterministic verification."
                : " candidate findings were dropped by deterministic verification.");
        }
    }

    private static IReadOnlyList<PrCommentThread> FilterThreadsForFile(
        IReadOnlyList<PrCommentThread>? allThreads,
        string filePath)
    {
        if (allThreads is null)
        {
            return [];
        }

        return allThreads.Where(t => t.FilePath == filePath || t.FilePath == null).ToList();
    }

    private static ReviewResult NormalizeCommentAnchors(ReviewResult result)
    {
        var normalizedComments = NormalizeCommentAnchors(result.Comments);
        return ReferenceEquals(normalizedComments, result.Comments)
            ? result
            : result with { Comments = normalizedComments };
    }

    private static IReadOnlyList<ReviewComment> NormalizeCommentAnchors(IReadOnlyList<ReviewComment> comments)
    {
        List<ReviewComment>? normalizedComments = null;

        for (var index = 0; index < comments.Count; index++)
        {
            var comment = comments[index];
            var normalizedComment = NormalizeCommentAnchor(comment);

            if (normalizedComments is null)
            {
                if (ReferenceEquals(normalizedComment, comment))
                {
                    continue;
                }

                normalizedComments = new List<ReviewComment>(comments.Count);
                for (var preservedIndex = 0; preservedIndex < index; preservedIndex++)
                {
                    normalizedComments.Add(comments[preservedIndex]);
                }
            }

            normalizedComments.Add(normalizedComment);
        }

        return normalizedComments is null ? comments : normalizedComments.AsReadOnly();
    }

    private static ReviewComment NormalizeCommentAnchor(ReviewComment comment)
    {
        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : FileByFileReviewOrchestrator.CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
    }

    private static int CountSeverityDowngrades(
        IReadOnlyList<ReviewComment> before,
        IReadOnlyList<ReviewComment> after)
    {
        var downgradedCount = 0;

        for (var i = 0; i < before.Count && i < after.Count; i++)
        {
            if (before[i].Severity != after[i].Severity)
            {
                downgradedCount++;
            }
        }

        return downgradedCount;
    }

}
