// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
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
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed partial class FileReviewer(
    IAiReviewCore aiCore,
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    AiReviewOptions options,
    ILogger<FileByFileReviewOrchestrator> logger,
    IReviewPipeline<PerFileReviewContext>? perFilePipeline,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IThreadMemoryService? memoryService,
    IAiRuntimeResolver? aiRuntimeResolver,
    CommentRelevanceFilterExecutor? commentRelevanceFilterExecutor,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    LocalReviewVerificationExecutor? localReviewVerificationExecutor,
    IReviewPipelineProfileProvider? pipelineProfileProvider,
    IReviewComplexityClassifier? complexityClassifier = null)
{
    private readonly ConcurrentDictionary<string, TriageVerdict> _triageCache = new(StringComparer.Ordinal);

    /// <summary>
    ///     Per-file complexity tier, model-judged via the injected classifier and cached so the baseline and
    ///     augmentation passes reuse one decision per file. Falls back to the size heuristic when no
    ///     classifier is configured (the classifier itself also falls back when the model is unavailable).
    /// </summary>
    private async Task<TriageVerdict> ClassifyTierAsync(ReviewJob job, ChangedFile file, ReviewSystemContext context, CancellationToken ct)
    {
        if (this._triageCache.TryGetValue(file.Path, out var cached))
        {
            return cached;
        }

        TriageVerdict verdict;
        if (complexityClassifier is null)
        {
            verdict = new TriageVerdict(ReviewDiffProcessor.ClassifyTier(file), false, "size-heuristic (no classifier configured)");
        }
        else
        {
            var fanOut = context.PerFileHint?.FanOut ?? FanOutSignal.Unavailable;
            var paths = context.PerFileHint?.AllChangedFileSummaries.Select(s => s.Path).ToList() ?? (IReadOnlyList<string>)[];
            verdict = await complexityClassifier.ClassifyAsync(job.ClientId, file, fanOut, paths, ct).ConfigureAwait(false);
        }

        this._triageCache[file.Path] = verdict;
        return verdict;
    }

    /// <summary>
    ///     Returns the model-judged triage verdict resolved for <paramref name="filePath" /> during review, or
    ///     <see langword="null" /> when the file has not been classified yet. Exposes the private triage cache so a
    ///     multi-pass wrapper can bind fan-out scope to the same resolved tier the review used.
    /// </summary>
    public TriageVerdict? TryGetResolvedTier(string filePath)
    {
        return this._triageCache.TryGetValue(filePath, out var verdict) ? verdict : null;
    }

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
        var triageVerdict = await this.ClassifyTierAsync(job, file, baseContext, ct);
        var tier = triageVerdict.Tier;

        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };

        var tierPurpose = GetTierPurpose(tier);

        var (tierClient, tierModelId, tierCapabilities) = await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

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
                tierCapabilities,
                effectiveClient,
                []);

            var pipelineProfile = ResolvePipelineProfile(job, pipelineProfileProvider);
            fileContext.Aggressiveness = pipelineProfile.Aggressiveness;
            await this.RecordPipelineProfileAsync(protocolId, file.Path, pipelineProfile, ct);
            fileContext = await this.RunDispatchPipelineAsync(
                job,
                file,
                fileResult,
                fileContext,
                protocolId,
                pipelineProfile,
                ct);

            await RecordFocusedGuidanceUsageAsync(protocolRecorder, protocolId, file.Path, fileContext, "per_file_review", ct);
            await this.RecordTriageDecisionEventAsync(fileContext, protocolId, file.Path, triageVerdict, ct);

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

            result = await this.RunReviewResultPipelineAsync(pipelineState, result, pipelineProfile, ct);

            // The full deterministic security floor for this file (path + content-marker + model-escalate legs).
            // A security-lens pass is scoped to flagged files regardless of tier, so it is carried into the fan-out
            // even when the main-path tier stays Low. RiskMarkers were populated by the dispatch pipeline above.
            var securityFlagged = SecurityFloor.IsFlagged(
                file.Path,
                fileContext.PerFileHint?.RiskMarkers ?? FileRiskMarkers.None,
                triageVerdict.SecurityEscalate);

            // When the client opts into multi-pass union, run additional independent passes and union their
            // locally-verified comments into this result before it is persisted, so the synthesis inlet sees the
            // union rather than a single pass. Ordinary passes run only on Medium/High tiers; a security-lens pass
            // runs on any tier when the file is security-flagged. Flag off (or nothing in scope) returns the
            // baseline result untouched — behavior is identical to a single-pass review.
            result = await this.MaybeApplyMultiPassUnionAsync(
                new MultiPassUnionInputs(
                    job,
                    pr,
                    filePr,
                    file,
                    fileIndex,
                    totalFiles,
                    fileContext,
                    protocolId,
                    tier,
                    securityFlagged,
                    tierModelId,
                    tierClient,
                    tierCapabilities,
                    effectiveClient,
                    pipelineProfile,
                    result,
                    ct));

            await this.CompleteReviewAsync(fileResult, fileContext, protocolId, result, ct);

            LogFileReviewCompleted(logger, file.Path, job.Id);
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
        AgentReviewRuntimeCapabilities? tierCapabilities,
        IChatClient effectiveClient,
        IReadOnlyList<FocusedReviewGuidanceItem> focusedReviewGuidance)
    {
        var enableProRvForCurrentPass = baseContext.AugmentationMode switch
        {
            // ProRVAugmentation passes still honor the client's ProRV opt-out: when EnableProRV is false the
            // high-risk second look runs WITHOUT the ProRV prefilter (the manual focused guidance still applies).
            _ when baseContext.PassKind == ReviewPassKind.ProRVAugmentation => baseContext.EnableProRV,
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
            PromptOverrides = baseContext.PromptOverrides,
            PerFileHint = new PerFileReviewHint(file.Path, fileIndex, totalFiles, pr.AllPrFileSummaries)
            {
                ComplexityTier = tier,
                MaxIterationsOverride = maxIterationsOverride,
                FocusedReviewGuidance = focusedReviewGuidance,
            },
            ExclusionRules = baseContext.ExclusionRules,
            DismissedPatterns = baseContext.DismissedPatterns,
            ModelId = tierModelId ?? baseContext.ModelId,
            RuntimeCapabilities = tierCapabilities ?? baseContext.RuntimeCapabilities,
            Temperature = baseContext.Temperature,
            EnableProRV = enableProRvForCurrentPass,
            EnableEvidenceBackedVerification = baseContext.EnableEvidenceBackedVerification,
            EnableLanguageRobustScreening = baseContext.EnableLanguageRobustScreening,
            EnableMultiPassUnion = baseContext.EnableMultiPassUnion,
            MultiPassUnionPassCount = baseContext.MultiPassUnionPassCount,
            ReviewPasses = baseContext.ReviewPasses,
            MultiPassDiversity = baseContext.MultiPassDiversity,
            AugmentationMode = baseContext.AugmentationMode,
            PassKind = baseContext.PassKind,
            PromptExperiment = baseContext.PromptExperiment,
            SkippedSteps = baseContext.SkippedSteps,
            // Tier-specific client wins; fall back to per-client active connection so the
            // global default chatClient is never used when a per-client connection is configured.
            TierChatClient = tierClient ?? effectiveClient,
        };

        LogDismissalsInjected(logger, fileContext.DismissedPatterns?.Count ?? 0, file.Path, job.Id);
        return fileContext;
    }

    public async Task<ReviewResult> ReviewAugmentationAsync(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct,
        string? reason = null,
        ReviewPassKind displayPassKind = ReviewPassKind.ProRVAugmentation,
        FanOutSignal? fanOut = null)
    {
        var verdict = await this.ClassifyTierAsync(job, file, baseContext, ct);

        // Deeper-pass tier = Max(model-judged tier, blast-radius floor, security floor). Floors are
        // escalate-only (Truncated fan-out and any security leg floor at Medium); for the ordinary ProRV
        // augmentation path fanOut is Unavailable and the security set is typically empty, so they are no-ops.
        var securityFlagged = SecurityFloor.IsFlagged(
            file.Path,
            baseContext.PerFileHint?.RiskMarkers ?? FileRiskMarkers.None,
            verdict.SecurityEscalate);
        var tier = TierJoin.Max(
            verdict.Tier,
            TierJoin.FloorFromFanOut(fanOut ?? FanOutSignal.Unavailable),
            securityFlagged ? FileComplexityTier.Medium : FileComplexityTier.Low);
        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };
        var tierPurpose = GetTierPurpose(tier);
        var (tierClient, tierModelId, tierCapabilities) = await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

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

        // Open a dedicated protocol pass for the augmentation/second-look so its AI calls, tokens,
        // tool calls, and findings are recorded. Previously this pass ran with protocolId=null and was
        // entirely unprotocolled (its tokens never reached the job aggregate). fileResultId is null —
        // the pass is identified by the file path.
        var protocolId = await this.BeginAugmentationProtocolAsync(job, file, tierCategory, tierModelId, reason, displayPassKind, ct);

        ReviewSystemContext? fileContext = null;
        try
        {
            fileContext = this.CreateFileContext(
                job,
                pr,
                file,
                fileIndex,
                totalFiles,
                augmentationContext,
                protocolId,
                tier,
                tierModelId,
                tierClient,
                tierCapabilities,
                effectiveClient,
                augmentationContext.PerFileHint?.FocusedReviewGuidance ?? []);

            var pipelineProfile = ResolvePipelineProfile(job, pipelineProfileProvider);
            fileContext = await this.RunDispatchPipelineAsync(
                job,
                file,
                transientFileResult,
                fileContext,
                protocolId,
                pipelineProfile,
                ct);

            var result = await this.ReviewFileCoreAsync(filePr, fileContext, ct);
            var pipelineState = new ReviewResultPipelineState(
                job,
                file,
                filePr,
                transientFileResult,
                fileContext,
                protocolId,
                reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? []);

            result = await this.RunReviewResultPipelineAsync(pipelineState, result, pipelineProfile, ct);

            await this.CompleteAugmentationProtocolAsync(protocolId, fileContext, "Completed", ct);
            return result;
        }
        catch when (protocolId.HasValue)
        {
            // Never leave the augmentation pass open on failure: close it so any partial loop-metric tokens
            // still reach the job aggregate. CancellationToken.None so cancellation still records completion.
            await this.CompleteAugmentationProtocolAsync(protocolId, fileContext, "Failed", CancellationToken.None);
            throw;
        }
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
            RuntimeCapabilities = baseContext.RuntimeCapabilities,
            Temperature = baseContext.Temperature,
            EnableProRV = baseContext.EnableProRV,
            EnableEvidenceBackedVerification = baseContext.EnableEvidenceBackedVerification,
            EnableLanguageRobustScreening = baseContext.EnableLanguageRobustScreening,
            EnableMultiPassUnion = baseContext.EnableMultiPassUnion,
            MultiPassUnionPassCount = baseContext.MultiPassUnionPassCount,
            ReviewPasses = baseContext.ReviewPasses,
            MultiPassDiversity = baseContext.MultiPassDiversity,
            AugmentationMode = baseContext.AugmentationMode,
            PassKind = ReviewPassKind.ProRVAugmentation,
            PerFileHint = baseContext.PerFileHint,
            PromptExperiment = baseContext.PromptExperiment,
            SkippedSteps = baseContext.SkippedSteps,
        };
    }

    /// <summary>
    ///     Runs additional independent passes over one file and unions their locally-verified comments with the
    ///     baseline pass when the client opted into multi-pass union and the file's resolved tier is Medium or High.
    ///     Low-tier files (and the flag-off path) return the baseline result unchanged, so the single-pass behavior
    ///     is byte-identical. Duplicates are not collapsed here — that happens downstream at the synthesis dedup
    ///     inlet; this step preserves every distinct finding produced by any pass.
    /// </summary>
    private async Task<ReviewResult> MaybeApplyMultiPassUnionAsync(MultiPassUnionInputs inputs)
    {
        if (!inputs.FileContext.EnableMultiPassUnion)
        {
            return inputs.BaselineResult;
        }

        // Ordinary/resample passes are gated to Medium/High tiers; a security-lens pass runs on any tier when the
        // file is security-flagged. Enter planning when either is in scope — the per-pass gate in the planners then
        // drops the passes that are out of scope, so a file that qualifies for neither is byte-identical to today.
        if (!IsMultiPassUnionTier(inputs.Tier) && !inputs.SecurityFlagged)
        {
            return inputs.BaselineResult;
        }

        var diversity = inputs.FileContext.MultiPassDiversity ?? MultiPassDiversity.Default;

        // Two ways the additional passes are chosen. The eval harness sets an explicit pass count and drives the
        // resamples from MultiPassDiversity arms over the tier connection. Production leaves the count null and runs
        // one pass per entry in the ordered per-client review-pass list, each resolved to its own configured model.
        var (plannedPasses, armLabel, skippedWholeFile) = inputs.FileContext.MultiPassUnionPassCount.HasValue
            ? this.PlanEvalResamplePasses(inputs, diversity)
            : await this.PlanReviewListResamplePassesAsync(inputs);

        // A whole-file skip (empty pass list) already recorded its trace; a plan with no runnable passes (single-pass
        // configuration, or every configured pass model unresolvable) degrades to the baseline without a completion.
        if (skippedWholeFile || plannedPasses.Count == 0)
        {
            return inputs.BaselineResult;
        }

        // The baseline pass is union pass 1; the planned passes are 2..k.
        var unionComments = new List<ReviewComment>(inputs.BaselineResult.Comments);
        var perPassCatchCounts = new List<int> { inputs.BaselineResult.Comments.Count };
        var perPassModels = new List<string?> { inputs.TierModelId };
        var perPassLenses = new List<string?> { null };

        foreach (var plannedPass in plannedPasses)
        {
            var passResult = await this.RunUnionResamplePassAsync(
                new MultiPassUnionPassInputs(
                    inputs.Job,
                    inputs.Pr,
                    inputs.FilePr,
                    inputs.File,
                    inputs.FileIndex,
                    inputs.TotalFiles,
                    inputs.FileContext,
                    inputs.Tier,
                    plannedPass.ModelId,
                    plannedPass.Client,
                    plannedPass.Capabilities,
                    inputs.EffectiveClient,
                    plannedPass.PassIndex,
                    diversity,
                    new MultiPassArm(plannedPass.Label, plannedPass.ModelId, plannedPass.Lens),
                    inputs.PipelineProfile,
                    inputs.Ct));

            unionComments.AddRange(StampUnionOrigin(passResult.Comments, plannedPass.PassIndex, plannedPass.Lens));
            perPassCatchCounts.Add(passResult.Comments.Count);
            perPassModels.Add(plannedPass.ModelId);
            perPassLenses.Add(plannedPass.Lens);
        }

        await this.RecordMultiPassUnionCompletedAsync(
            new MultiPassUnionCompletion(
                inputs.ProtocolId,
                inputs.File.Path,
                armLabel,
                inputs.Tier,
                perPassCatchCounts,
                perPassModels,
                perPassLenses,
                unionComments.Count,
                inputs.Ct));

        return inputs.BaselineResult with { Comments = unionComments };
    }

    // Eval-harness path: the resample passes come from MultiPassDiversity arms and reuse the file's tier connection,
    // switching only the model id (an arm override, or the tier model when the arm carries none).
    private (IReadOnlyList<PlannedResamplePass> Passes, string ArmLabel, bool SkippedWholeFile) PlanEvalResamplePasses(
        MultiPassUnionInputs inputs,
        MultiPassDiversity diversity)
    {
        var passCount = Math.Max(1, inputs.FileContext.MultiPassUnionPassCount!.Value);
        var armLabel = diversity.ResolveArmLabel();
        if (passCount <= 1)
        {
            return ([], armLabel, false);
        }

        var tierEligible = IsMultiPassUnionTier(inputs.Tier);
        var resamplePlan = diversity.ResolveResamplePasses(passCount - 1);
        var passes = new List<PlannedResamplePass>(resamplePlan.Count);
        for (var index = 0; index < resamplePlan.Count; index++)
        {
            var arm = resamplePlan[index];

            // A lens arm runs on any tier when the file is security-flagged; a plain resample arm runs only on the
            // in-scope (Medium/High) tiers. Dropping out-of-scope arms keeps non-lens eval control configs identical.
            var inScope = arm.Lens is not null ? inputs.SecurityFlagged : tierEligible;
            if (!inScope)
            {
                continue;
            }

            passes.Add(
                new PlannedResamplePass(
                    index + 2,
                    arm.Label,
                    arm.ModelId ?? inputs.TierModelId,
                    inputs.TierClient,
                    inputs.TierCapabilities,
                    arm.Lens));
        }

        return (passes, armLabel, false);
    }

    // Production path: one resample per entry in the ordered per-client review-pass list, each resolved to its own
    // configured model (connection implied). An empty list records the whole-file skip; an unresolvable entry records
    // a per-pass skip and is dropped while the rest run — never a same-tier-model resample fallback.
    private async Task<(IReadOnlyList<PlannedResamplePass> Passes, string ArmLabel, bool SkippedWholeFile)> PlanReviewListResamplePassesAsync(
        MultiPassUnionInputs inputs)
    {
        const string ReviewPassListLabel = "review-pass-list";
        var reviewPasses = inputs.FileContext.ReviewPasses;
        if (reviewPasses.Count == 0)
        {
            await this.RecordMultiPassUnionSkippedAsync(
                inputs.ProtocolId,
                inputs.File.Path,
                ReviewPassListLabel,
                inputs.Tier,
                "empty_review_pass_list",
                inputs.Ct);
            return ([], ReviewPassListLabel, true);
        }

        var tierEligible = IsMultiPassUnionTier(inputs.Tier);
        var passes = new List<PlannedResamplePass>(reviewPasses.Count);
        var passIndex = 2;
        foreach (var pass in reviewPasses)
        {
            // A security-lens entry runs on any tier when the file is security-flagged; an ordinary entry runs only
            // on the in-scope (Medium/High) tiers. Out-of-scope entries are dropped without a trace (the common case
            // is a lens configured on a client whose file under review is not security-relevant), but still consume
            // their ordinal so a pass's "Pass N" index stays tied to its position in the configured list.
            var isSecurityLens = pass.Lens == ReviewPassLens.Security;
            var inScope = isSecurityLens ? inputs.SecurityFlagged : tierEligible;
            if (!inScope)
            {
                passIndex++;
                continue;
            }

            var runtime = await this.TryResolvePassRuntimeAsync(inputs.Job, inputs.File.Path, pass.ConfiguredModelId, passIndex, inputs.Ct);
            if (runtime is null)
            {
                await this.RecordMultiPassUnionPassSkippedAsync(inputs.ProtocolId, inputs.File.Path, passIndex, pass.ConfiguredModelId, inputs.Tier, inputs.Ct);
            }
            else
            {
                passes.Add(
                    new PlannedResamplePass(
                        passIndex,
                        runtime.Model.RemoteModelId,
                        runtime.Model.RemoteModelId,
                        runtime.ChatClient,
                        runtime.Capabilities,
                        pass.Lens));
            }

            passIndex++;
        }

        return (passes, ReviewPassListLabel, false);
    }

    private static bool IsMultiPassUnionTier(FileComplexityTier tier)
    {
        return tier is FileComplexityTier.Medium or FileComplexityTier.High;
    }

    // Tags a resample pass's comments with the multi-pass union origin and the 1-based pass index so the union
    // source and the specific numbered pass are visible on the persisted per-file result and surface through the
    // protocol comment DTO (the frontend renders the index as "Pass N"). The baseline pass is pass 1 and stays
    // unstamped; the additional passes are 2..k.
    private static IReadOnlyList<ReviewComment> StampUnionOrigin(IReadOnlyList<ReviewComment> comments, int passIndex, string? lens)
    {
        if (comments.Count == 0)
        {
            return comments;
        }

        var stamped = new List<ReviewComment>(comments.Count);
        foreach (var comment in comments)
        {
            stamped.Add(
                comment with
                {
                    OriginPassKind = ReviewPassKind.MultiPassUnion.ToString(),
                    OriginPassIndex = passIndex,
                    OriginPassLens = lens,
                });
        }

        return stamped;
    }

    private async Task<ReviewResult> RunUnionResamplePassAsync(MultiPassUnionPassInputs inputs)
    {
        var tierCategory = TierCategory(inputs.Tier);
        var protocolId = await this.BeginAugmentationProtocolAsync(
            inputs.Job,
            inputs.File,
            tierCategory,
            inputs.PassModelId,
            $"multi-pass union {inputs.PassArm.Label} pass #{inputs.PassIndex}",
            ReviewPassKind.MultiPassUnion,
            inputs.Ct);

        // A transient result row keeps this pass from overwriting the persisted per-file result; only its
        // returned comments are unioned by the caller before the single persistence write.
        var transientFileResult = new ReviewFileResult(inputs.Job.Id, inputs.File.Path);
        ReviewSystemContext? passContext = null;
        try
        {
            passContext = this.CreateFileContext(
                inputs.Job,
                inputs.Pr,
                inputs.File,
                inputs.FileIndex,
                inputs.TotalFiles,
                inputs.FileContext,
                protocolId,
                inputs.Tier,
                inputs.PassModelId,
                inputs.PassClient,
                inputs.PassCapabilities,
                inputs.EffectiveClient,
                inputs.FileContext.PerFileHint?.FocusedReviewGuidance ?? []);

            // Each resample pass runs at the resampling temperature. The pass model + client + capabilities were
            // already resolved by the caller and threaded in here (an eval-harness arm-model override reusing the
            // tier connection, or a production review-pass model resolved to its own runtime), so CreateFileContext
            // has set them — the baseline pass keeps the tier model, only these resample passes switch, so any recall
            // lift comes from a different sampler reviewing the file, not from re-sampling the same one.
            passContext.Temperature = inputs.Diversity.ResampleTemperature;

            // A lens pass selects a specialist per-file prompt; the marker is read during prompt construction.
            passContext.ActiveLens = inputs.PassArm.Lens;

            passContext = await this.RunDispatchPipelineAsync(
                inputs.Job,
                inputs.File,
                transientFileResult,
                passContext,
                protocolId,
                inputs.PipelineProfile,
                inputs.Ct);

            var result = await this.ReviewFileCoreAsync(inputs.FilePr, passContext, inputs.Ct);
            var pipelineState = new ReviewResultPipelineState(
                inputs.Job,
                inputs.File,
                inputs.FilePr,
                transientFileResult,
                passContext,
                protocolId,
                reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? []);

            result = await this.RunReviewResultPipelineAsync(pipelineState, result, inputs.PipelineProfile, inputs.Ct);
            await this.CompleteAugmentationProtocolAsync(protocolId, passContext, "Completed", inputs.Ct);
            return result;
        }
        catch when (protocolId.HasValue)
        {
            await this.CompleteAugmentationProtocolAsync(protocolId, passContext, "Failed", CancellationToken.None);
            throw;
        }
    }

    private async Task RecordMultiPassUnionCompletedAsync(MultiPassUnionCompletion completion)
    {
        if (!completion.ProtocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            completion.ProtocolId.Value,
            ReviewProtocolEventNames.MultiPassUnionCompleted,
            JsonSerializer.Serialize(
                new
                {
                    filePath = completion.FilePath,
                    tier = completion.Tier.ToString(),
                    arm = completion.ArmLabel,
                    passCount = completion.PerPassCatchCounts.Count,
                }),
            JsonSerializer.Serialize(
                new
                {
                    perPassCatchCounts = completion.PerPassCatchCounts,
                    perPassModels = completion.PerPassModels,
                    perPassLenses = completion.PerPassLenses,
                    unionCount = completion.UnionCount,
                }),
            null,
            completion.Ct);
    }

    private static AiConnectionModelCategory TierCategory(FileComplexityTier tier)
    {
        return tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };
    }

    // Resolves the runtime for one production resample pass from its configured model id (connection implied).
    // Returns null when the model cannot be resolved (deleted/unresolved model, or no resolver) so the caller skips
    // that pass rather than resampling the tier model.
    private async Task<IResolvedAiChatRuntime?> TryResolvePassRuntimeAsync(
        ReviewJob job,
        string filePath,
        Guid configuredModelId,
        int passIndex,
        CancellationToken ct)
    {
        if (aiRuntimeResolver is null)
        {
            LogMultiPassUnionPassModelUnresolved(logger, job.Id, filePath, configuredModelId, passIndex, null);
            return null;
        }

        try
        {
            return await aiRuntimeResolver.ResolveChatRuntimeForModelAsync(job.ClientId, configuredModelId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMultiPassUnionPassModelUnresolved(logger, job.Id, filePath, configuredModelId, passIndex, ex);
            return null;
        }
    }

    private async Task RecordMultiPassUnionSkippedAsync(
        Guid? protocolId,
        string filePath,
        string armLabel,
        FileComplexityTier tier,
        string reason,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.MultiPassUnionSkipped,
            JsonSerializer.Serialize(
                new
                {
                    filePath,
                    tier = tier.ToString(),
                    arm = armLabel,
                }),
            JsonSerializer.Serialize(
                new
                {
                    reason,
                }),
            null,
            ct);
    }

    private async Task RecordMultiPassUnionPassSkippedAsync(
        Guid? protocolId,
        string filePath,
        int passIndex,
        Guid configuredModelId,
        FileComplexityTier tier,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.MultiPassUnionPassSkipped,
            JsonSerializer.Serialize(
                new
                {
                    filePath,
                    tier = tier.ToString(),
                    passIndex,
                }),
            JsonSerializer.Serialize(
                new
                {
                    configuredModelId,
                    reason = "pass_model_unresolved",
                }),
            null,
            ct);
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

    private async Task<bool> TryRecordSkippedStepAsync(ReviewResultPipelineState state, string stepId, CancellationToken ct)
    {
        if (!state.FileContext.SkippedSteps.Contains(stepId))
        {
            return false;
        }

        if (state.ProtocolId.HasValue)
        {
            await protocolRecorder.RecordReviewStrategyEventAsync(
                state.ProtocolId.Value,
                ReviewProtocolEventNames.ReviewStepSkipped,
                JsonSerializer.Serialize(new { stepId, scope = "file", filePath = state.File.Path }),
                JsonSerializer.Serialize(new { skipped = true }),
                null,
                ct);
        }

        return true;
    }

    internal async Task<ReviewSystemContext> RunDispatchPipelineAsync(
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

    internal static ReviewPipelineProfile ResolvePipelineProfile(
        ReviewJob job,
        IReviewPipelineProfileProvider? pipelineProfileProvider)
    {
        if (pipelineProfileProvider is null)
        {
            return new ReviewPipelineProfile(
                ReviewPipelineProfileCatalog.FileByFileBalancedProfileId,
                "Balanced",
                ReviewStrategy.FileByFile,
                [
                    FileByFileContextPrefetchStage.StageIdConstant,
                    FileByFileRiskMarkerStage.StageIdConstant,
                    FileByFileProRvPrefilterStage.StageIdConstant,
                ],
                [
                    FileByFileConfidenceFloorStage.StageIdConstant,
                    FileByFileInfoCommentStripStage.StageIdConstant,
                    FileByFileSelfReflectionRankingStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                true,
                ReviewAggressiveness.Balanced,
                10);
        }

        var profiles = pipelineProfileProvider.GetProfiles(ReviewStrategy.FileByFile);
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
                   ReviewPipelineProfileCatalog.FileByFileBalancedProfileId,
                   "Balanced",
                   ReviewStrategy.FileByFile,
                   [
                       FileByFileContextPrefetchStage.StageIdConstant,
                       FileByFileRiskMarkerStage.StageIdConstant,
                       FileByFileProRvPrefilterStage.StageIdConstant,
                   ],
                   [
                       FileByFileConfidenceFloorStage.StageIdConstant,
                       FileByFileInfoCommentStripStage.StageIdConstant,
                       FileByFileSelfReflectionRankingStage.StageIdConstant,
                   ],
                   [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                   true,
                   ReviewAggressiveness.Balanced,
                   10);
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

    private static async Task RecordFocusedGuidanceUsageAsync(
        IProtocolRecorder protocolRecorder,
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

    private async Task<ReviewResult> ApplyCommentRelevanceFilterStageAsync(
        ReviewResultPipelineState state,
        ReviewResult result,
        CancellationToken ct)
    {
        if (await this.TryRecordSkippedStepAsync(state, FileByFileReviewStepIds.CommentRelevanceFilter, ct))
        {
            return result;
        }

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
        if (await this.TryRecordSkippedStepAsync(state, FileByFileReviewStepIds.MemoryReconsideration, ct))
        {
            return result;
        }

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
        if (await this.TryRecordSkippedStepAsync(state, FileByFileReviewStepIds.LocalVerification, ct))
        {
            return result;
        }

        if (localReviewVerificationExecutor is null)
        {
            return result;
        }

        // Supply the per-file context so an evidence-gathering verifier can read the anchor code,
        // judge with the file's tier client, and substantiate (or refute) a withheld claim.
        var verificationContext = new ReviewVerificationContext(
            state.FileContext.ReviewTools,
            state.FilePullRequest.SourceBranch,
            state.FileContext.TierChatClient,
            state.FileContext.ModelId,
            state.Job.ClientId,
            aiRuntimeResolver,
            state.FileContext.EnableEvidenceBackedVerification);

        return await localReviewVerificationExecutor.ApplyAsync(
            result,
            state.FileResult,
            state.ProtocolId,
            state.InvariantFacts,
            verificationContext,
            ct);
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

        var beforeInfo = result.Comments.Count;
        result = ReviewCommentProcessing.StripInfoComments(result);
        var infoDropped = beforeInfo - result.Comments.Count;
        if (infoDropped > 0)
        {
            LogInfoCommentsDropped(logger, infoDropped, file.Path, job.Id);
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
                ct,
                m.TotalCachedInputTokens,
                m.TotalCachedInputTokens > 0
                    ? CacheObservabilityStatus.Observable
                    : CacheObservabilityStatus.Unobservable);
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

    private async Task<Guid?> BeginAugmentationProtocolAsync(
        ReviewJob job,
        ChangedFile file,
        AiConnectionModelCategory tierCategory,
        string? tierModelId,
        string? reason,
        ReviewPassKind displayPassKind,
        CancellationToken ct)
    {
        try
        {
            return await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                file.Path,
                null,
                tierCategory,
                tierModelId,
                ct,
                displayPassKind,
                reason);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
            return null;
        }
    }

    private async Task CompleteAugmentationProtocolAsync(
        Guid? protocolId,
        ReviewSystemContext? fileContext,
        string outcome,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        // Complete even when loop metrics are absent (e.g. the AI call threw before stamping them) so the
        // pass is never left open; missing metrics record as zeros.
        var m = fileContext?.LoopMetrics;
        var cached = m?.TotalCachedInputTokens ?? 0;
        await protocolRecorder.SetCompletedAsync(
            protocolId.Value,
            outcome,
            m?.TotalInputTokens ?? 0,
            m?.TotalOutputTokens ?? 0,
            m?.Iterations ?? 0,
            m?.ToolCallCount ?? 0,
            m?.FinalConfidence,
            ct,
            cached,
            cached > 0 ? CacheObservabilityStatus.Observable : CacheObservabilityStatus.Unobservable);
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
                ct,
                ReviewPassKind.Baseline);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
            return null;
        }
    }

    private async Task RecordTriageDecisionEventAsync(
        ReviewSystemContext fileContext,
        Guid? protocolId,
        string filePath,
        TriageVerdict verdict,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        // Persist the per-file triage decision as a structured protocol event (exposed via the
        // job-protocol API for trace display + audit). Absence is explicit: fanOutKind distinguishes
        // Unavailable from a measured count, and fanOutCount is null when there is no measurement.
        var fanOut = fileContext.PerFileHint?.FanOut ?? FanOutSignal.Unavailable;
        var riskMarkers = fileContext.PerFileHint?.RiskMarkers ?? FileRiskMarkers.None;
        var securityFlagged = SecurityFloor.IsFlagged(filePath, riskMarkers, verdict.SecurityEscalate);

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.TriageDecision,
            JsonSerializer.Serialize(
                new
                {
                    filePath,
                    tier = verdict.Tier.ToString(),
                    why = verdict.Why,
                    securityEscalate = verdict.SecurityEscalate,
                    securityFlagged,
                    fanOutKind = fanOut.Kind.ToString(),
                    fanOutCount = fanOut.HasData ? fanOut.Count : (int?)null,
                }),
            null,
            null,
            ct);
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

    private async Task<(IChatClient? tierClient, string? tierModelId, AgentReviewRuntimeCapabilities? tierCapabilities)> ResolveTierClientAsync(
        ReviewJob job,
        AiConnectionModelCategory tierCategory,
        AiPurpose tierPurpose,
        CancellationToken ct)
    {
        IChatClient? tierClient = null;
        string? tierModelId = null;
        AgentReviewRuntimeCapabilities? tierCapabilities = null;

        if (aiRuntimeResolver is not null)
        {
            try
            {
                var tierRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, tierPurpose, ct);
                tierClient = tierRuntime.ChatClient;
                tierModelId = tierRuntime.Model.RemoteModelId;
                tierCapabilities = tierRuntime.Capabilities;
            }
            catch
            {
                tierClient = null;
                tierModelId = null;
                tierCapabilities = null;
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

        return (tierClient, tierModelId, tierCapabilities);
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

    private sealed record ReviewResultPipelineState(
        ReviewJob Job,
        ChangedFile File,
        PullRequest FilePullRequest,
        ReviewFileResult FileResult,
        ReviewSystemContext FileContext,
        Guid? ProtocolId,
        IReadOnlyList<InvariantFact> InvariantFacts);

    // One planned resample pass (pass 2..k): its index, provenance label, resolved model id, the client +
    // capabilities it runs on, and an optional specialist lens. Eval passes reuse the tier connection; production
    // passes carry their own runtime.
    private sealed record PlannedResamplePass(
        int PassIndex,
        string Label,
        string? ModelId,
        IChatClient? Client,
        AgentReviewRuntimeCapabilities? Capabilities,
        string? Lens = null);

    private sealed record MultiPassUnionCompletion(
        Guid? ProtocolId,
        string FilePath,
        string ArmLabel,
        FileComplexityTier Tier,
        IReadOnlyList<int> PerPassCatchCounts,
        IReadOnlyList<string?> PerPassModels,
        IReadOnlyList<string?> PerPassLenses,
        int UnionCount,
        CancellationToken Ct);

    private sealed record MultiPassUnionInputs(
        ReviewJob Job,
        PullRequest Pr,
        PullRequest FilePr,
        ChangedFile File,
        int FileIndex,
        int TotalFiles,
        ReviewSystemContext FileContext,
        Guid? ProtocolId,
        FileComplexityTier Tier,
        bool SecurityFlagged,
        string? TierModelId,
        IChatClient? TierClient,
        AgentReviewRuntimeCapabilities? TierCapabilities,
        IChatClient EffectiveClient,
        ReviewPipelineProfile PipelineProfile,
        ReviewResult BaselineResult,
        CancellationToken Ct);

    private sealed record MultiPassUnionPassInputs(
        ReviewJob Job,
        PullRequest Pr,
        PullRequest FilePr,
        ChangedFile File,
        int FileIndex,
        int TotalFiles,
        ReviewSystemContext FileContext,
        FileComplexityTier Tier,
        string? PassModelId,
        IChatClient? PassClient,
        AgentReviewRuntimeCapabilities? PassCapabilities,
        IChatClient EffectiveClient,
        int PassIndex,
        MultiPassDiversity Diversity,
        MultiPassArm PassArm,
        ReviewPipelineProfile PipelineProfile,
        CancellationToken Ct);
}
