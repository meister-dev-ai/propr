// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed partial class AgenticFileReviewer(
    IAiReviewCore aiCore,
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    AiReviewOptions options,
    ILogger<AgenticFileByFileReviewOrchestrator> logger,
    IReviewPipeline<PerFileReviewContext>? perFilePipeline,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IThreadMemoryService? memoryService,
    IAiRuntimeResolver? aiRuntimeResolver,
    CommentRelevanceFilterExecutor? commentRelevanceFilterExecutor,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    LocalReviewVerificationExecutor? localReviewVerificationExecutor,
    IReviewPipelineProfileProvider? pipelineProfileProvider)
{
    public async Task<IReadOnlyList<CandidateReviewFinding>> ReviewAsync(
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

        var tier = ReviewDiffProcessor.ClassifyTier(file);

        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };

        var tierPurpose = GetTierPurpose(tier);

        var (tierClient, tierModelId) = await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

        var protocolId = await this.BeginNewProtocolAsync(job, file, fileResult, tierCategory, tierModelId, ct);

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
                effectiveClient,
                []);

            var pipelineProfile = ResolvePipelineProfile(job, pipelineProfileProvider);
            await this.RecordPipelineProfileAsync(protocolId, file.Path, pipelineProfile, ct);
            fileContext = await this.RunDispatchPipelineAsync(
                job,
                file,
                fileResult,
                fileContext,
                protocolId,
                pipelineProfile,
                ct);

            await this.RecordFocusedGuidanceUsageAsync(protocolId, file.Path, fileContext, "agentic_file_planning", ct);

            fileContext = await this.EnrichContextWithAgenticArtifactsAsync(job, pr, filePr, file, fileContext, effectiveClient, ct);

            var result = await this.ReviewFileCoreAsync(filePr, fileContext, ct);

            var agenticCandidateFindings = localReviewVerificationExecutor is not { IsEnabled: true }
                ? []
                : GetAgenticCandidateFindings(fileContext);
            result = MergeAgenticCandidateComments(result, agenticCandidateFindings);

            var pipelineState = new ReviewResultPipelineState(
                job,
                file,
                filePr,
                fileResult,
                fileContext,
                protocolId,
                reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? []);

            result = await this.RunReviewResultPipelineAsync(pipelineState, result, pipelineProfile, ct);

            var survivingAgenticCandidateFindings = fileContext.PerFileHint?.VerifiedAgenticCandidateFindings ?? [];

            await this.CompleteReviewAsync(fileResult, fileContext, protocolId, result, ct);

            LogFileReviewCompleted(logger, file.Path, job.Id);
            return survivingAgenticCandidateFindings;
        }
        catch (Exception ex)
        {
            await this.FailReviewAsync(fileResult, fileContext, protocolId, ex, ct);
            throw;
        }
    }

    private async Task<ReviewFileResult> InitializeFileResultAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewFileResult? existingResult,
        CancellationToken ct)
    {
        if (existingResult is { IsComplete: false })
        {
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
        IChatClient effectiveClient,
        IReadOnlyList<FocusedReviewGuidanceItem> focusedReviewGuidance)
    {
        var enableProRvForCurrentPass = baseContext.AugmentationMode switch
        {
            _ when baseContext.PassKind == ReviewPassKind.ProRVAugmentation => true,
            ReviewAugmentationMode.LateAugmentation => false,
            ReviewAugmentationMode.Disabled => false,
            _ => baseContext.EnableProRV,
        };

        var changedLinesCount = ReviewDiffProcessor.CountChangedLines(file.UnifiedDiff);
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
                FocusedReviewGuidance = focusedReviewGuidance,
            },
            ExclusionRules = baseContext.ExclusionRules,
            DismissedPatterns = baseContext.DismissedPatterns,
            PromptOverrides = baseContext.PromptOverrides,
            ModelId = tierModelId ?? baseContext.ModelId,
            Temperature = baseContext.Temperature,
            EnableProRV = enableProRvForCurrentPass,
            AugmentationMode = baseContext.AugmentationMode,
            PassKind = baseContext.PassKind,
            TierChatClient = tierClient ?? effectiveClient,
        };

        LogDismissalsInjected(logger, fileContext.DismissedPatterns?.Count ?? 0, file.Path, job.Id);
        return fileContext;
    }

    private async Task<ReviewSystemContext> EnrichContextWithAgenticArtifactsAsync(
        ReviewJob job,
        PullRequest pr,
        PullRequest filePr,
        ChangedFile file,
        ReviewSystemContext fileContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var plan = await this.CreateAgenticPlanAsync(job, pr, filePr, file, fileContext, effectiveClient, ct);
        var investigations = await this.RunAgenticInvestigationsAsync(job, filePr, fileContext, plan, effectiveClient, ct);

        return this.CloneFileContextWithAgenticArtifacts(fileContext, plan, investigations);
    }

    private ReviewSystemContext CloneFileContextWithAgenticArtifacts(
        ReviewSystemContext fileContext,
        AgenticFileReviewPlan plan,
        IReadOnlyList<AgenticFileInvestigationResult> investigations)
    {
        var hint = fileContext.PerFileHint;
        return new ReviewSystemContext(fileContext.ClientSystemMessage, fileContext.RepositoryInstructions, fileContext.ReviewTools)
        {
            LoopMetrics = fileContext.LoopMetrics,
            ActiveProtocolId = fileContext.ActiveProtocolId,
            ProtocolRecorder = fileContext.ProtocolRecorder,
            ExclusionRules = fileContext.ExclusionRules,
            DismissedPatterns = fileContext.DismissedPatterns,
            PromptOverrides = fileContext.PromptOverrides,
            TierChatClient = fileContext.TierChatClient,
            ModelId = fileContext.ModelId,
            DefaultReviewChatClient = fileContext.DefaultReviewChatClient,
            DefaultReviewModelId = fileContext.DefaultReviewModelId,
            Temperature = fileContext.Temperature,
            EnableProRV = fileContext.EnableProRV,
            AugmentationMode = fileContext.AugmentationMode,
            PassKind = fileContext.PassKind,
            PerFileHint = hint is null ? null : hint with { AgenticPlan = plan, AgenticInvestigations = investigations },
        };
    }

    public async Task<ReviewResult> ReviewAugmentationAsync(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var tier = ReviewDiffProcessor.ClassifyTier(file);
        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };
        var tierPurpose = GetTierPurpose(tier);
        var (tierClient, tierModelId) = await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

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

        var augmentationContext = CreateAugmentationContext(baseContext);
        var transientFileResult = new ReviewFileResult(job.Id, file.Path);
        var fileContext = this.CreateFileContext(
            job,
            pr,
            file,
            fileIndex,
            totalFiles,
            augmentationContext,
            null,
            tier,
            tierModelId,
            tierClient,
            effectiveClient,
            []);

        var pipelineProfile = ResolvePipelineProfile(job, pipelineProfileProvider);
        fileContext = await this.RunDispatchPipelineAsync(
            job,
            file,
            transientFileResult,
            fileContext,
            null,
            pipelineProfile,
            ct);
        fileContext = await this.EnrichContextWithAgenticArtifactsAsync(job, pr, filePr, file, fileContext, effectiveClient, ct);

        var result = await this.ReviewFileCoreAsync(filePr, fileContext, ct);
        result = MergeAgenticCandidateComments(result, GetAgenticCandidateFindings(fileContext));

        var pipelineState = new ReviewResultPipelineState(
            job,
            file,
            filePr,
            transientFileResult,
            fileContext,
            null,
            reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? []);

        return await this.RunReviewResultPipelineAsync(pipelineState, result, pipelineProfile, ct);
    }

    private static ReviewSystemContext CreateAugmentationContext(ReviewSystemContext baseContext)
    {
        return new ReviewSystemContext(baseContext.ClientSystemMessage, baseContext.RepositoryInstructions, baseContext.ReviewTools)
        {
            LoopMetrics = baseContext.LoopMetrics,
            ActiveProtocolId = baseContext.ActiveProtocolId,
            ProtocolRecorder = baseContext.ProtocolRecorder,
            ExclusionRules = baseContext.ExclusionRules,
            DismissedPatterns = baseContext.DismissedPatterns,
            PromptOverrides = baseContext.PromptOverrides,
            TierChatClient = baseContext.TierChatClient,
            ModelId = baseContext.ModelId,
            DefaultReviewChatClient = baseContext.DefaultReviewChatClient,
            DefaultReviewModelId = baseContext.DefaultReviewModelId,
            Temperature = baseContext.Temperature,
            EnableProRV = true,
            AugmentationMode = baseContext.AugmentationMode,
            PassKind = ReviewPassKind.ProRVAugmentation,
            PerFileHint = baseContext.PerFileHint,
        };
    }

    private static IReadOnlyList<AgenticFileCandidateFinding> GetAgenticCandidateFindings(ReviewSystemContext fileContext)
    {
        return fileContext.PerFileHint?.AgenticInvestigations
                   .Where(investigation => !investigation.DiagnosticsOnly)
                   .SelectMany(investigation => investigation.CandidateFindings)
                   .ToList()
               ?? [];
    }

    private static ReviewResult MergeAgenticCandidateComments(
        ReviewResult result,
        IReadOnlyList<AgenticFileCandidateFinding> agenticCandidateFindings)
    {
        if (agenticCandidateFindings.Count == 0)
        {
            return result;
        }

        var mergedComments = result.Comments.ToList();
        var seenKeys = mergedComments
            .Select(CreateAgenticCommentMatchKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in agenticCandidateFindings)
        {
            var candidateComment = AgenticFileByFileReviewOrchestrator.CreateReviewComment(
                candidate.FilePath,
                candidate.LineNumber,
                candidate.Severity,
                candidate.Message);
            if (seenKeys.Add(CreateAgenticCommentMatchKey(candidateComment)))
            {
                mergedComments.Add(candidateComment);
            }
        }

        return mergedComments.Count == result.Comments.Count
            ? result
            : result with { Comments = mergedComments };
    }

    private static IReadOnlyList<CandidateReviewFinding> BuildSurvivingAgenticCandidateFindings(
        ReviewFileResult fileResult,
        IReadOnlyList<ReviewComment> finalComments,
        IReadOnlyList<AgenticFileCandidateFinding> originalAgenticCandidateFindings)
    {
        if (finalComments.Count == 0 || originalAgenticCandidateFindings.Count == 0)
        {
            return [];
        }

        var candidatesByKey = new Dictionary<string, Queue<AgenticFileCandidateFinding>>(StringComparer.Ordinal);
        foreach (var candidate in originalAgenticCandidateFindings)
        {
            var key = CreateAgenticCommentMatchKey(candidate.FilePath, candidate.LineNumber, candidate.Message);
            if (!candidatesByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<AgenticFileCandidateFinding>();
                candidatesByKey[key] = queue;
            }

            queue.Enqueue(candidate);
        }

        var surviving = new List<CandidateReviewFinding>();
        for (var index = 0; index < finalComments.Count; index++)
        {
            var comment = finalComments[index];
            var key = CreateAgenticCommentMatchKey(comment);
            if (!candidatesByKey.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                continue;
            }

            var candidate = queue.Dequeue();
            surviving.Add(
                new CandidateReviewFinding(
                    AgenticFileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, index + 1),
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.DeeperFollowUpOrigin,
                        "agentic_file_investigation",
                        fileResult.FilePath,
                        fileResult.Id,
                        index + 1,
                        candidate.EvidenceReference.EvidenceSource,
                        true,
                        candidate.Id),
                    comment.Severity,
                    comment.Message,
                    candidate.Category,
                    comment.FilePath,
                    AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber),
                    candidate.EvidenceReference,
                    candidate.CandidateSummaryText,
                    candidate.InvariantCheckContext));
        }

        return surviving;
    }

    private static string CreateAgenticCommentMatchKey(ReviewComment comment)
    {
        return CreateAgenticCommentMatchKey(comment.FilePath, comment.LineNumber, comment.Message);
    }

    private static string CreateAgenticCommentMatchKey(string? filePath, int? lineNumber, string message)
    {
        return string.Concat(
            filePath ?? string.Empty,
            "|",
            AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(lineNumber)?.ToString() ?? string.Empty,
            "|",
            message);
    }

    private async Task<AgenticFileReviewPlan> CreateAgenticPlanAsync(
        ReviewJob job,
        PullRequest pr,
        PullRequest filePr,
        ChangedFile file,
        ReviewSystemContext fileContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        try
        {
            var messages = new[]
            {
                new ChatMessage(ChatRole.System, ReviewPrompts.BuildAgenticFilePlanningSystemPrompt(fileContext)),
                new ChatMessage(ChatRole.User, ReviewPrompts.BuildAgenticFilePlanningUserMessage(file, filePr)),
            };
            var response = await effectiveClient.GetResponseAsync(messages, new ChatOptions { ModelId = fileContext.ModelId }, ct);
            await this.RecordAiStageResponseAsync(fileContext, messages, response, "agentic_file_planning", ct);

            if (AgenticFileArtifactParser.TryParsePlan(response.Text, out var parsedPlan) && parsedPlan is not null)
            {
                await this.RecordAgenticFileStageEventAsync(
                    fileContext,
                    ReviewProtocolEventNames.AgenticFilePlanCreated,
                    new { strategy = "agentic_file_by_file", stage = "planning", jobId = job.Id, file = file.Path },
                    parsedPlan,
                    ct);
                return parsedPlan;
            }
        }
        catch (Exception ex)
        {
            LogAgenticStageFallback(logger, "planning", file.Path, job.Id, ex.Message);
            await this.RecordAgenticFileStageEventAsync(
                fileContext,
                ReviewProtocolEventNames.AgenticFileDegraded,
                new { strategy = "agentic_file_by_file", stage = "planning", jobId = job.Id, file = file.Path },
                new { reason = ex.Message },
                ct);
        }

        var triggerProfile = CreateFallbackTriggerProfile(pr, file.Path);
        var changedAreas = triggerProfile.ChangedAreas;
        IReadOnlyList<AgenticFileInvestigationTask> tasks = triggerProfile.TriggerFamily is null
            ? []
            :
            [
                new AgenticFileInvestigationTask(
                    "task-001",
                    "concern",
                    triggerProfile.TriggerFamily,
                    triggerProfile.Concern,
                    triggerProfile.SeedFiles,
                    triggerProfile.AllowedTools,
                    triggerProfile.MaxToolCalls),
            ];

        var fallbackPlan = new AgenticFileReviewPlan(
            $"plan-{SanitizeId(file.Path)}",
            file.Path,
            [triggerProfile.Concern],
            changedAreas,
            tasks,
            tasks.Count == 0 ? "No explicit trigger family required deeper follow-up for this straightforward file." : null);

        await this.RecordAgenticFileStageEventAsync(
            fileContext,
            ReviewProtocolEventNames.AgenticFilePlanCreated,
            new { strategy = "agentic_file_by_file", stage = "planning", jobId = job.Id, file = file.Path },
            fallbackPlan,
            ct);
        return fallbackPlan;
    }

    private async Task<IReadOnlyList<AgenticFileInvestigationResult>> RunAgenticInvestigationsAsync(
        ReviewJob job,
        PullRequest filePr,
        ReviewSystemContext fileContext,
        AgenticFileReviewPlan plan,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        if (plan.InvestigationTasks.Count == 0)
        {
            return [];
        }

        var results = new List<AgenticFileInvestigationResult>(plan.InvestigationTasks.Count);
        foreach (var task in plan.InvestigationTasks)
        {
            await this.RecordAgenticFileStageEventAsync(
                fileContext,
                ReviewProtocolEventNames.AgenticFileInvestigationLaunched,
                new { strategy = "agentic_file_by_file", stage = "investigation", jobId = job.Id, anchorFile = plan.AnchorFilePath, taskId = task.TaskId },
                new { task.AllowedTools, task.MaxToolCalls, task.SeedFilePaths },
                ct);

            var result = await this.RunSingleAgenticInvestigationAsync(filePr, fileContext, plan, task, effectiveClient, ct);
            results.Add(result);

            if (result.Evidence.Count > 0)
            {
                await this.RecordAgenticFileStageEventAsync(
                    fileContext,
                    ReviewProtocolEventNames.AgenticFileEvidenceCollected,
                    new { strategy = "agentic_file_by_file", stage = "investigation", taskId = task.TaskId },
                    new
                    {
                        evidenceCount = result.Evidence.Count,
                        evidence = result.Evidence.Select(item => new { item.Kind, item.SourceId, item.Summary }).ToList(),
                        toolUsage = result.ToolUsage,
                    },
                    ct);
            }

            if (result.DiagnosticsOnly)
            {
                await this.RecordAgenticFileStageEventAsync(
                    fileContext,
                    ReviewProtocolEventNames.AgenticFileFollowUpDiagnosticsOnly,
                    new { strategy = "agentic_file_by_file", stage = "investigation", taskId = task.TaskId, anchorFile = plan.AnchorFilePath },
                    new { result.Status, result.Degraded, result.DiagnosticsOnly, result.EvidenceSetId, candidateCount = result.CandidateFindings.Count },
                    ct);
            }

            await this.RecordAgenticFileStageEventAsync(
                fileContext,
                result.Degraded ? ReviewProtocolEventNames.AgenticFileDegraded : ReviewProtocolEventNames.AgenticFileInvestigationResult,
                new { strategy = "agentic_file_by_file", stage = "investigation", taskId = task.TaskId, anchorFile = plan.AnchorFilePath },
                new
                {
                    result.Status, result.ToolUsage, result.Degraded, result.DiagnosticsOnly, result.EvidenceSetId,
                    candidateCount = result.CandidateFindings.Count,
                },
                ct);
        }

        return results;
    }

    private async Task<AgenticFileInvestigationResult> RunSingleAgenticInvestigationAsync(
        PullRequest filePr,
        ReviewSystemContext fileContext,
        AgenticFileReviewPlan plan,
        AgenticFileInvestigationTask task,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var reviewTools = fileContext.ReviewTools;
        if (reviewTools is null)
        {
            return new AgenticFileInvestigationResult(
                task.TaskId,
                "degraded",
                [],
                [],
                [],
                true,
                true,
                $"evidence-{task.TaskId}");
        }

        var evidence = new List<EvidenceItem>();
        var canPrefetchSeedContent = task.AllowedTools.Any(tool => string.Equals(
            tool,
            BoundedReviewContextTools.GetFileContentToolName,
            StringComparison.Ordinal));
        if (canPrefetchSeedContent)
        {
            foreach (var filePath in task.SeedFilePaths)
            {
                try
                {
                    var content = await reviewTools.GetFileContentAsync(filePath, filePr.SourceBranch, 1, options.FileBatchLines, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        evidence.Add(new EvidenceItem("file_content", $"Captured bounded context for {filePath}.", filePath));
                    }
                }
                catch (Exception ex)
                {
                    LogAgenticStageFallback(logger, "investigation_seed_fetch", plan.AnchorFilePath, Guid.Empty, ex.Message);
                }
            }
        }

        var boundedTools = new BoundedReviewContextTools(reviewTools, task.AllowedTools, task.MaxToolCalls, task.SeedFilePaths);

        try
        {
            var responseText = await this.RunBoundedInvestigationLoopAsync(
                fileContext,
                filePr,
                plan,
                task,
                effectiveClient,
                boundedTools,
                ct);

            if (AgenticFileArtifactParser.TryParseInvestigationResult(responseText, task, out var parsed) && parsed is not null)
            {
                return ApplyAuthoritativeToolUsage(
                    parsed with
                    {
                        DiagnosticsOnly = parsed.DiagnosticsOnly || parsed.Degraded,
                        EvidenceSetId = string.IsNullOrWhiteSpace(parsed.EvidenceSetId) ? $"evidence-{task.TaskId}" : parsed.EvidenceSetId,
                    },
                    boundedTools.Attempts);
            }
        }
        catch (Exception ex)
        {
            LogAgenticStageFallback(logger, "investigation", plan.AnchorFilePath, Guid.Empty, ex.Message);
        }

        var fallback = new AgenticFileInvestigationResult(
            task.TaskId,
            "degraded",
            evidence,
            [],
            [],
            true,
            true,
            $"evidence-{task.TaskId}");

        return ApplyAuthoritativeToolUsage(fallback, boundedTools.Attempts);
    }

    private async Task<string> RunBoundedInvestigationLoopAsync(
        ReviewSystemContext fileContext,
        PullRequest filePr,
        AgenticFileReviewPlan plan,
        AgenticFileInvestigationTask task,
        IChatClient effectiveClient,
        BoundedReviewContextTools boundedTools,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReviewPrompts.BuildAgenticFileInvestigationSystemPrompt(fileContext)),
            new(ChatRole.User, ReviewPrompts.BuildAgenticFileInvestigationUserMessage(plan, task, filePr)),
        };

        var registeredTools = BuildBoundedInvestigationTools(boundedTools, ct);
        var chatOptions = new ChatOptions
        {
            ModelId = fileContext.ModelId,
            Temperature = fileContext.Temperature,
            Tools = registeredTools.Count > 0 ? [.. registeredTools] : null,
        };

        var maxIterations = Math.Max(2, Math.Min(options.MaxIterations, task.MaxToolCalls + 2));
        var lastTextResponse = string.Empty;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var response = await effectiveClient.GetResponseAsync(messages, chatOptions, ct);
            var responseMessage = response.Messages.Last();
            await this.RecordAiStageResponseAsync(
                fileContext,
                messages,
                response,
                $"agentic_file_investigation_{task.TaskId}_iter_{iteration}",
                ct);

            messages.AddRange(response.Messages);

            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                lastTextResponse = response.Text;
            }

            var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count == 0)
            {
                break;
            }

            var toolResultContents = new List<AIContent>(functionCalls.Count);
            foreach (var call in functionCalls)
            {
                var argumentsJson = JsonSerializer.Serialize(call.Arguments);
                var resultText = await this.InvokeBoundedInvestigationToolAsync(call, registeredTools, ct);
                toolResultContents.Add(new FunctionResultContent(call.CallId, resultText));

                if (fileContext.ActiveProtocolId.HasValue && fileContext.ProtocolRecorder is not null)
                {
                    await fileContext.ProtocolRecorder.RecordToolCallAsync(
                        fileContext.ActiveProtocolId.Value,
                        call.Name ?? string.Empty,
                        argumentsJson,
                        resultText,
                        iteration,
                        ct);
                }
            }

            messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));
        }

        if (!string.IsNullOrWhiteSpace(lastTextResponse))
        {
            return lastTextResponse;
        }

        messages.Add(
            new ChatMessage(
                ChatRole.User,
                "Provide the final Stage B investigation result now as a single raw JSON object matching the required schema. Do not call tools. Do not add markdown fences."));

        var finalResponse = await effectiveClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                ModelId = fileContext.ModelId,
                Temperature = fileContext.Temperature,
            },
            ct);

        await this.RecordAiStageResponseAsync(
            fileContext,
            messages,
            finalResponse,
            $"agentic_file_investigation_{task.TaskId}_final",
            ct);

        return finalResponse.Text ?? string.Empty;
    }

    private static AgenticFileInvestigationResult ApplyAuthoritativeToolUsage(
        AgenticFileInvestigationResult parsed,
        IReadOnlyList<PrWideToolUsage> attempts)
    {
        var authoritativeUsage = attempts
            .Select(attempt => new AgenticFileToolUsage(attempt.ToolName, attempt.Status, attempt.Target))
            .ToList();

        var degradedByRuntime = authoritativeUsage.Any(usage => !string.Equals(
            usage.Status, BoundedReviewContextTools.SuccessStatus, StringComparison.Ordinal));
        var status = parsed.Status;
        if (degradedByRuntime && !string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
        {
            status = "degraded";
        }

        var degraded = parsed.Degraded || degradedByRuntime;
        var diagnosticsOnly = parsed.DiagnosticsOnly
                              || degraded
                              || !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

        return parsed with
        {
            Status = status,
            ToolUsage = authoritativeUsage,
            Degraded = degraded,
            DiagnosticsOnly = diagnosticsOnly,
        };
    }

    private static List<AIFunction> BuildBoundedInvestigationTools(IReviewContextTools reviewTools, CancellationToken cancellationToken)
    {
        var supportsProCursorTools = reviewTools is not IProCursorAvailabilityAware { SupportsProCursorTools: false };

        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                () => reviewTools.GetChangedFilesAsync(cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = BoundedReviewContextTools.GetChangedFilesToolName,
                    Description = "Get the list of files changed in this pull request.",
                }),
            AIFunctionFactory.Create(
                (string branch) => reviewTools.GetFileTreeAsync(branch, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = BoundedReviewContextTools.GetFileTreeToolName,
                    Description = "Get the repository file tree for the specified branch.",
                }),
            AIFunctionFactory.Create(
                (string path, string branch, int startLine, int endLine) =>
                    reviewTools.GetFileContentAsync(path, branch, startLine, endLine, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = BoundedReviewContextTools.GetFileContentToolName,
                    Description = "Get file content for a bounded line range from the PR source branch.",
                }),
        };

        if (supportsProCursorTools)
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string question) => reviewTools.AskProCursorKnowledgeAsync(question, cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = BoundedReviewContextTools.AskProCursorKnowledgeToolName,
                        Description = "Ask ProCursor a repository-aware knowledge question.",
                    }));

            tools.Add(
                AIFunctionFactory.Create(
                    (string symbol, string? queryMode, int? maxRelations) =>
                        reviewTools.GetProCursorSymbolInfoAsync(symbol, queryMode, maxRelations, cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = BoundedReviewContextTools.GetProCursorSymbolInfoToolName,
                        Description = "Ask ProCursor for repository-aware symbol insight.",
                    }));
        }

        return tools;
    }

    private async Task<string> InvokeBoundedInvestigationToolAsync(
        FunctionCallContent call,
        IReadOnlyList<AIFunction> tools,
        CancellationToken cancellationToken)
    {
        var matchingTool = tools.FirstOrDefault(tool => tool.Name == call.Name);
        if (matchingTool is null)
        {
            return $"[Unknown tool: {call.Name}]";
        }

        try
        {
            var functionArgs = call.Arguments is null
                ? null
                : new AIFunctionArguments(call.Arguments);
            var result = await matchingTool.InvokeAsync(functionArgs, cancellationToken);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            LogAgenticStageFallback(logger, "investigation_tool_call", call.Name ?? string.Empty, Guid.Empty, ex.Message);
            return $"[Tool error: {ex.Message}]";
        }
    }

    private async Task RecordAiStageResponseAsync(
        ReviewSystemContext fileContext,
        IReadOnlyList<ChatMessage> messages,
        ChatResponse response,
        string eventName,
        CancellationToken ct)
    {
        if (!fileContext.ActiveProtocolId.HasValue || fileContext.ProtocolRecorder is null)
        {
            return;
        }

        await fileContext.ProtocolRecorder.RecordAiCallAsync(
            fileContext.ActiveProtocolId.Value,
            1,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text,
            messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text,
            response.Text,
            ct,
            eventName);
    }

    private async Task RecordAgenticFileStageEventAsync(
        ReviewSystemContext fileContext,
        string eventName,
        object details,
        object output,
        CancellationToken ct)
    {
        if (!fileContext.ActiveProtocolId.HasValue || fileContext.ProtocolRecorder is null)
        {
            return;
        }

        await fileContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            fileContext.ActiveProtocolId.Value,
            eventName,
            JsonSerializer.Serialize(details),
            JsonSerializer.Serialize(output),
            null,
            ct);
    }

    private static bool IsLikelySibling(string path, string anchorFilePath)
    {
        if (string.Equals(path, anchorFilePath, StringComparison.Ordinal))
        {
            return true;
        }

        var anchorDirectory = Path.GetDirectoryName(anchorFilePath);
        var candidateDirectory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(anchorDirectory) &&
               string.Equals(anchorDirectory, candidateDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAgenticConcern(string filePath)
    {
        if (filePath.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("Registration", StringComparison.OrdinalIgnoreCase))
        {
            return "Check whether dependency registration changes require sibling context or test coverage updates.";
        }

        if (filePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return "Check whether test changes still match the behavioral contract under review.";
        }

        return $"Check behavioral impact around {filePath}.";
    }

    private static FallbackTriggerProfile CreateFallbackTriggerProfile(PullRequest pr, string anchorFilePath)
    {
        var changedPaths = pr.AllPrFileSummaries
            .Select(summary => summary.Path)
            .ToList();
        var sameDirectoryPaths = changedPaths
            .Where(path => IsLikelySibling(path, anchorFilePath))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var concern = BuildAgenticConcern(anchorFilePath);
        if (pr.AllPrFileSummaries.Count <= 1)
        {
            return new FallbackTriggerProfile(concern, [anchorFilePath], [anchorFilePath], null, [], 0);
        }

        if (anchorFilePath.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            anchorFilePath.Contains("Registration", StringComparison.OrdinalIgnoreCase))
        {
            var registrationRelated = changedPaths
                .Where(path => string.Equals(path, anchorFilePath, StringComparison.Ordinal) ||
                               path.Contains("Registration", StringComparison.OrdinalIgnoreCase) ||
                               IsLikelySibling(path, anchorFilePath))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var seedFiles = registrationRelated.Count > 0 ? registrationRelated : [anchorFilePath];
            return new FallbackTriggerProfile(
                concern,
                seedFiles,
                seedFiles,
                "bounded_sibling_context",
                [BoundedReviewContextTools.GetFileContentToolName, BoundedReviewContextTools.GetFileTreeToolName],
                2);
        }

        if (anchorFilePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            var seedFiles = sameDirectoryPaths.Count > 0 ? sameDirectoryPaths : [anchorFilePath];
            return new FallbackTriggerProfile(
                concern,
                seedFiles,
                seedFiles,
                "bounded_cross_file_consistency",
                [BoundedReviewContextTools.GetFileContentToolName],
                1);
        }

        return new FallbackTriggerProfile(concern, [anchorFilePath], [anchorFilePath], null, [], 0);
    }

    private static string SanitizeId(string value)
    {
        return string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')).Trim('-');
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
        ReviewPipelineProfile pipelineProfile,
        CancellationToken ct)
    {
        if (perFilePipeline is not null)
        {
            var pipelineContext = await perFilePipeline.ExecuteAsync(
                new PerFileReviewContext(
                    state.Job,
                    state.File,
                    state.FileResult,
                    state.FileContext,
                    state.ProtocolId,
                    null,
                    result),
                pipelineProfile.PerFileStageIds,
                ct);
            result = pipelineContext.ReviewResult ?? result;
        }
        else
        {
            result = this.ApplyReviewFilters(state.Job, state.File, result, state.FileContext);
        }

        result = await this.ApplyCommentRelevanceFilterStageAsync(state, result, ct);
        result = await this.ApplyMemoryReconsiderationStageAsync(state, result, ct);
        result = NormalizeCommentAnchors(result);
        return await this.ApplyLocalVerificationStageAsync(state, result, ct);
    }

    private async Task<ReviewSystemContext> RunDispatchPipelineAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewFileResult fileResult,
        ReviewSystemContext fileContext,
        Guid? protocolId,
        ReviewPipelineProfile pipelineProfile,
        CancellationToken ct)
    {
        if (perFilePipeline is null || pipelineProfile.DispatchStageIds.Count == 0)
        {
            return fileContext;
        }

        var pipelineContext = await perFilePipeline.ExecuteAsync(
            new PerFileReviewContext(
                job,
                file,
                fileResult,
                fileContext,
                protocolId,
                null,
                null),
            pipelineProfile.DispatchStageIds,
            ct);

        return pipelineContext.FileReviewContext;
    }

    private async Task RecordPipelineProfileAsync(
        Guid? protocolId,
        string filePath,
        ReviewPipelineProfile pipelineProfile,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.ReviewPipelineProfileApplied,
            JsonSerializer.Serialize(
                new
                {
                    filePath,
                    profileId = pipelineProfile.ProfileId,
                    strategy = pipelineProfile.Strategy.ToString(),
                }),
            JsonSerializer.Serialize(
                new
                {
                    pipelineProfile.ProfileId,
                    pipelineProfile.DispatchStageIds,
                    pipelineProfile.PerFileStageIds,
                    pipelineProfile.FinalizationStageIds,
                }),
            null,
            ct);
    }

    private async Task RecordFocusedGuidanceUsageAsync(
        Guid? protocolId,
        string filePath,
        ReviewSystemContext fileContext,
        string promptKind,
        CancellationToken ct)
    {
        if (!protocolId.HasValue || !fileContext.EnableProRV)
        {
            return;
        }

        var guidance = fileContext.PerFileHint?.FocusedReviewGuidance ?? [];
        await protocolRecorder.RecordProRvEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.ProRVFocusedGuidanceApplied,
            JsonSerializer.Serialize(
                new
                {
                    filePath,
                    promptKind,
                    applied = guidance.Count > 0,
                    guidanceCount = guidance.Count,
                }),
            JsonSerializer.Serialize(
                new
                {
                    guidanceIds = guidance.Select(item => item.Id).ToArray(),
                }),
            null,
            ct);
    }

    private static ReviewPipelineProfile ResolvePipelineProfile(
        ReviewJob job,
        IReviewPipelineProfileProvider? pipelineProfileProvider)
    {
        if (pipelineProfileProvider is null)
        {
            return new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.AgenticBaselineProfileId,
                "Agentic baseline",
                ReviewStrategy.AgenticFileByFile,
                [AgenticProRvPrefilterStage.StageIdConstant],
                [
                    AgenticConfidenceFloorStage.StageIdConstant,
                    AgenticSpeculativeCommentFilterStage.StageIdConstant,
                    AgenticInfoCommentStripStage.StageIdConstant,
                    AgenticVagueSuggestionFilterStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                true);
        }

        var profiles = pipelineProfileProvider.GetProfiles(ReviewStrategy.AgenticFileByFile);
        if (!string.IsNullOrWhiteSpace(job.ReviewPipelineProfileId))
        {
            var selected = profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, job.ReviewPipelineProfileId, StringComparison.Ordinal));
            if (selected is not null)
            {
                return selected;
            }
        }

        return profiles.FirstOrDefault(profile => profile.IsBaseline)
               ?? profiles.FirstOrDefault()
               ?? new ReviewPipelineProfile(
                   ReviewPipelineProfileProvider.AgenticBaselineProfileId,
                   "Agentic baseline",
                   ReviewStrategy.AgenticFileByFile,
                   [AgenticProRvPrefilterStage.StageIdConstant],
                   [
                       AgenticConfidenceFloorStage.StageIdConstant,
                       AgenticSpeculativeCommentFilterStage.StageIdConstant,
                       AgenticInfoCommentStripStage.StageIdConstant,
                       AgenticVagueSuggestionFilterStage.StageIdConstant,
                   ],
                   [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                   true);
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
        if (localReviewVerificationExecutor is null)
        {
            return result;
        }

        var verification = await localReviewVerificationExecutor.ApplyDetailedAsync(
            result,
            state.FileResult,
            state.ProtocolId,
            state.InvariantFacts,
            GetDetailedAgenticCandidateFindings(state.FileContext),
            ct);

        state.FileContext.PerFileHint = state.FileContext.PerFileHint is null
            ? null
            : state.FileContext.PerFileHint with { VerifiedAgenticCandidateFindings = verification.VerifiedCandidateFindings };
        return verification.Result;
    }

    private static IReadOnlyList<CandidateReviewFinding> GetDetailedAgenticCandidateFindings(ReviewSystemContext fileContext)
    {
        var investigations = fileContext.PerFileHint?.AgenticInvestigations;
        var triggerFamiliesByTaskId = fileContext.PerFileHint?.AgenticPlan?.InvestigationTasks
                                          .Where(task => !string.IsNullOrWhiteSpace(task.TaskId) && !string.IsNullOrWhiteSpace(task.TriggerFamily))
                                          .ToDictionary(task => task.TaskId, task => task.TriggerFamily, StringComparer.Ordinal)
                                      ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (investigations is null || investigations.Count == 0)
        {
            return [];
        }

        return investigations
            .Where(investigation => !investigation.DiagnosticsOnly)
            .SelectMany(investigation => investigation.CandidateFindings.Select(candidate =>
            {
                var finding = candidate.ToCandidateReviewFinding(
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.DeeperFollowUpOrigin,
                        "agentic_file_investigation",
                        candidate.FilePath,
                        evidenceSetId: investigation.EvidenceSetId,
                        requiresExplicitSupport: true,
                        sourceOriginId: investigation.TaskId),
                    candidate.Id);

                triggerFamiliesByTaskId.TryGetValue(investigation.TaskId, out var triggerFamily);
                return new CandidateReviewFinding(
                    finding.FindingId,
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    finding.Evidence,
                    finding.CandidateSummaryText,
                    MergeFollowUpTriggerFamily(finding.InvariantCheckContext, triggerFamily),
                    finding.VerificationOutcome);
            }))
            .ToList();
    }

    private static IReadOnlyDictionary<string, string>? MergeFollowUpTriggerFamily(
        IReadOnlyDictionary<string, string>? existingContext,
        string? triggerFamily)
    {
        if (string.IsNullOrWhiteSpace(triggerFamily))
        {
            return existingContext;
        }

        var merged = existingContext is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existingContext, StringComparer.Ordinal);
        merged[CandidateReviewFinding.FollowUpTriggerFamilyContextKey] = triggerFamily;
        return merged;
    }

    private ReviewResult ApplyReviewFilters(
        ReviewJob job,
        ChangedFile file,
        ReviewResult result,
        ReviewSystemContext fileContext)
    {
        var commentsBeforeConfidenceFloor = result.Comments;
        result = ReviewCommentProcessing.ApplyConfidenceFloor(result, fileContext.LoopMetrics?.FinalConfidence, options);
        var confidenceDroppedCount = CountSeverityDowngrades(commentsBeforeConfidenceFloor, result.Comments);
        if (confidenceDroppedCount > 0)
        {
            LogSeverityDowngraded(logger, confidenceDroppedCount, file.Path, job.Id);
        }

        var beforeHedge = result.Comments.Count;
        result = ReviewCommentProcessing.FilterSpeculativeComments(result);
        var hedgeDropped = beforeHedge - result.Comments.Count;
        if (hedgeDropped > 0)
        {
            LogSpeculativeCommentsDropped(logger, hedgeDropped, file.Path, job.Id);
        }

        var beforeInfo = result.Comments.Count;
        result = ReviewCommentProcessing.StripInfoComments(result);
        var infoDropped = beforeInfo - result.Comments.Count;
        if (infoDropped > 0)
        {
            LogInfoCommentsDropped(logger, infoDropped, file.Path, job.Id);
        }

        var beforeVague = result.Comments.Count;
        result = ReviewCommentProcessing.FilterVagueSuggestions(result);
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
        var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : AgenticFileByFileReviewOrchestrator.CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
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

    private sealed record FallbackTriggerProfile(
        string Concern,
        IReadOnlyList<string> ChangedAreas,
        IReadOnlyList<string> SeedFiles,
        string? TriggerFamily,
        IReadOnlyList<string> AllowedTools,
        int MaxToolCalls);

    private sealed record ReviewResultPipelineState(
        ReviewJob Job,
        ChangedFile File,
        PullRequest FilePullRequest,
        ReviewFileResult FileResult,
        ReviewSystemContext FileContext,
        Guid? ProtocolId,
        IReadOnlyList<InvariantFact> InvariantFacts);
}
