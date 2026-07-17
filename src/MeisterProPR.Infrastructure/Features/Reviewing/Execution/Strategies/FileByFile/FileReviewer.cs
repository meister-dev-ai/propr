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
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Models;
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
    IProRVPrefilter? proRvPrefilter = null,
    IReviewComplexityClassifier? complexityClassifier = null)
{
    // Stage id recorded on the ProRV-lens applicability screen's protocol events.
    private const string ProRvLensStageId = "file-by-file.prorv-lens";

    // The embedded ProRV catalog is compiled into the ProRV assembly, so its assembly version is a stable token
    // that changes only when the assets change — used to key the per-job focused-guidance cache.
    private static readonly string ProRvCatalogVersion =
        typeof(IProRVPrefilter).Assembly.GetName().Version?.ToString() ?? "0";

    private readonly ConcurrentDictionary<string, TriageVerdict> _triageCache = new(StringComparer.Ordinal);

    // Focused ProRV guidance is deterministic per (file path, catalog version) within a job, so a prorv lens pass
    // that re-screens the same file reuses the ranking instead of paying a second model call.
    private readonly ConcurrentDictionary<string, IReadOnlyList<FocusedReviewGuidanceItem>> _proRvGuidanceCache =
        new(StringComparer.Ordinal);

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

        var (tierClient, tierModelId, tierCapabilities, tierMaxContextTokens, tierTokenizerName) =
            await this.ResolveTierClientAsync(job, tierCategory, tierPurpose, ct);

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
                tierMaxContextTokens,
                tierTokenizerName,
                effectiveClient,
                []);

            var pipelineProfile = ResolvePipelineProfile(job, pipelineProfileProvider);
            fileContext.Aggressiveness = pipelineProfile.Aggressiveness;
            // The baseline (tier) pass takes the client-level baseline reasoning effort. Set before the dispatch
            // pipeline so it survives on the same context the review core reads.
            fileContext.ActiveReasoningEffort = baseContext.BaselineReasoningEffort;
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
        int? tierMaxContextTokens,
        string? tierTokenizerName,
        IChatClient effectiveClient,
        IReadOnlyList<FocusedReviewGuidanceItem> focusedReviewGuidance)
    {
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
            MaxContextTokens = tierMaxContextTokens ?? baseContext.MaxContextTokens,
            TokenizerName = tierTokenizerName ?? baseContext.TokenizerName,
            RuntimeCapabilities = tierCapabilities ?? baseContext.RuntimeCapabilities,
            Temperature = baseContext.Temperature,
            EnableEvidenceBackedVerification = baseContext.EnableEvidenceBackedVerification,
            EnableLanguageRobustScreening = baseContext.EnableLanguageRobustScreening,
            EnableMultiPassUnion = baseContext.EnableMultiPassUnion,
            MultiPassUnionPassCount = baseContext.MultiPassUnionPassCount,
            ReviewPasses = baseContext.ReviewPasses,
            MultiPassDiversity = baseContext.MultiPassDiversity,
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
        // file is security-flagged; a prorv-lens pass runs on any tier when the file is catalog-eligible. Enter
        // planning when any is in scope — the per-pass gate in the planners then drops the passes that are out of
        // scope, so a file that qualifies for none is byte-identical to today. A pr_wide-scope pass never runs per
        // file, so it is excluded here — a pass list of only pr_wide entries is not a reason to fan out per-file.
        var hasEligibleProRvPass = proRvPrefilter is not null
                                   && IsProRvEligible(inputs.File)
                                   && inputs.FileContext.ReviewPasses.Any(pass =>
                                       pass.Lens == ReviewPassLens.ProRV && pass.Scope != ReviewPassScope.PrWide);
        if (!IsMultiPassUnionTier(inputs.Tier) && !inputs.SecurityFlagged && !hasEligibleProRvPass)
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
                    plannedPass.MaxContextTokens,
                    plannedPass.TokenizerName,
                    plannedPass.ReasoningEffort,
                    inputs.Ct));

            unionComments.AddRange(StampUnionOrigin(passResult.Comments, plannedPass.PassIndex, plannedPass.Lens, plannedPass.Shadow));
            perPassCatchCounts.Add(passResult.Comments.Count);
            perPassModels.Add(plannedPass.ModelId);
            perPassLenses.Add(plannedPass.Lens);

            // A shadow pass runs and its findings are unioned into the persisted per-file result for the trace, but
            // they carry the shadow marker so synthesis drops them from the publishable set. Record its catch count.
            if (plannedPass.Shadow)
            {
                await this.RecordPassShadowCompletedAsync(
                    inputs.ProtocolId,
                    inputs.File.Path,
                    plannedPass.PassIndex,
                    plannedPass.ModelId,
                    passResult.Comments.Count,
                    inputs.Ct);
            }
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
            // A pr_wide-scope entry runs once at the job level over the whole change set, not per file, so this
            // per-file planner skips it — like an out-of-scope entry — while still consuming its ordinal so a pass's
            // "Pass N" index stays tied to its position in the configured list. PR-wide execution is handled
            // separately; here the entry simply contributes nothing to the per-file union.
            if (pass.Scope == ReviewPassScope.PrWide)
            {
                passIndex++;
                continue;
            }

            // A security-lens entry runs on any tier when the file is security-flagged; a ProRV-lens entry runs on any
            // tier when the file is deterministically catalog-eligible (a text file with a diff — the model ranking
            // that picks the applicable checks, and decides whether any apply, runs inside the pass); an ordinary
            // entry runs only on the in-scope (Medium/High) tiers. Out-of-scope entries are dropped without a trace
            // (the common case is a lens configured on a client whose file under review is not relevant) but still
            // consume their ordinal so a pass's "Pass N" index stays tied to its position in the configured list.
            bool inScope;
            if (pass.Lens == ReviewPassLens.Security)
            {
                inScope = inputs.SecurityFlagged;
            }
            else if (pass.Lens == ReviewPassLens.ProRV)
            {
                inScope = proRvPrefilter is not null && IsProRvEligible(inputs.File);
            }
            else
            {
                inScope = tierEligible;
            }

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
                        pass.Lens,
                        pass.Shadow,
                        runtime.Model.MaxContextTokens,
                        runtime.Model.TokenizerName,
                        pass.ReasoningEffort));
            }

            passIndex++;
        }

        return (passes, ReviewPassListLabel, false);
    }

    private static bool IsMultiPassUnionTier(FileComplexityTier tier)
    {
        return tier is FileComplexityTier.Medium or FileComplexityTier.High;
    }

    // Cheap deterministic ProRV eligibility screen (no model call): a text file with an actual diff. The model
    // ranking that decides which catalog checks apply — and whether any do — runs inside the pass.
    private static bool IsProRvEligible(ChangedFile file)
    {
        return !file.IsBinary
               && file.ChangeType != ChangeType.Delete
               && !string.IsNullOrWhiteSpace(file.UnifiedDiff);
    }

    // Tags a resample pass's comments with the multi-pass union origin and the 1-based pass index so the union
    // source and the specific numbered pass are visible on the persisted per-file result and surface through the
    // protocol comment DTO (the frontend renders the index as "Pass N"). The baseline pass is pass 1 and stays
    // unstamped; the additional passes are 2..k.
    private static IReadOnlyList<ReviewComment> StampUnionOrigin(IReadOnlyList<ReviewComment> comments, int passIndex, string? lens, bool shadow)
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
                    OriginPassShadow = shadow,
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
            var focusedGuidance = inputs.FileContext.PerFileHint?.FocusedReviewGuidance ?? [];

            // A ProRV-lens pass screens the file against the embedded catalog first. The applicability ranking is a
            // single model call on this pass's own configured model; it both gates the pass and produces the focused
            // guidance. No applicable check (or a deterministic ineligibility) skips the review with a reason-coded
            // trace and NO review model call — the pass contributes nothing to the union.
            if (string.Equals(inputs.PassArm.Lens, ReviewPassLens.ProRV, StringComparison.Ordinal))
            {
                focusedGuidance = await this.ResolveProRvLensGuidanceAsync(inputs, protocolId, inputs.Ct);
                if (focusedGuidance.Count == 0)
                {
                    await this.CompleteAugmentationProtocolAsync(protocolId, null, "Completed", inputs.Ct);
                    return new ReviewResult(string.Empty, []);
                }
            }

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
                inputs.PassMaxContextTokens ?? inputs.FileContext.MaxContextTokens,
                inputs.PassTokenizerName ?? inputs.FileContext.TokenizerName,
                inputs.EffectiveClient,
                focusedGuidance);

            // Each resample pass runs at the resampling temperature. The pass model + client + capabilities were
            // already resolved by the caller and threaded in here (an eval-harness arm-model override reusing the
            // tier connection, or a production review-pass model resolved to its own runtime), so CreateFileContext
            // has set them — the baseline pass keeps the tier model, only these resample passes switch, so any recall
            // lift comes from a different sampler reviewing the file, not from re-sampling the same one.
            passContext.Temperature = inputs.Diversity.ResampleTemperature;

            // A lens pass selects a specialist per-file prompt; the marker is read during prompt construction.
            passContext.ActiveLens = inputs.PassArm.Lens;

            // The pass's configured reasoning effort is applied to the outbound request unconditionally.
            passContext.ActiveReasoningEffort = inputs.PassReasoningEffort;

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

    // Resolves ProRV focused guidance for a prorv-lens pass by ranking the file against the embedded catalog on the
    // pass's own configured model (one model call). Cached per (file path, model id, catalog version) within the job
    // so a repeated prorv pass over the same file on the same model reuses the ranking; the model id is part of the
    // key because the ranking is a model call, not deterministic across models. Only a successful ranking is cached,
    // so a transient failure or a skip never poisons a later pass. Returns an empty list when the file is ineligible
    // or no catalog check applies — the caller then skips the pass without a review model call.
    private async Task<IReadOnlyList<FocusedReviewGuidanceItem>> ResolveProRvLensGuidanceAsync(
        MultiPassUnionPassInputs inputs,
        Guid? protocolId,
        CancellationToken ct)
    {
        var cacheKey = $"{inputs.File.Path}::{inputs.PassModelId}::{ProRvCatalogVersion}";
        if (this._proRvGuidanceCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolution = await ProRVFocusedReviewGuidanceResolver.TryResolveAsync(
            inputs.Job,
            inputs.File,
            inputs.PassClient ?? inputs.EffectiveClient,
            inputs.PassModelId,
            protocolId,
            protocolRecorder,
            proRvPrefilter,
            logger,
            ProRvLensStageId,
            ct);

        // Only cache a completed ranking; a skip/failure returns empty guidance but must not short-circuit a
        // subsequent pass whose model might succeed.
        if (resolution.PrefilterStatus == ProRVPrefilterStatus.Success)
        {
            this._proRvGuidanceCache[cacheKey] = resolution.Guidance;
        }

        return resolution.Guidance;
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

    private async Task RecordPassShadowCompletedAsync(
        Guid? protocolId,
        string filePath,
        int passIndex,
        string? modelId,
        int catchCount,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.PassShadowCompleted,
            JsonSerializer.Serialize(
                new
                {
                    scope = "per_file",
                    filePath,
                    passIndex,
                }),
            JsonSerializer.Serialize(
                new
                {
                    modelId,
                    catchCount,
                    published = false,
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
                [
                    FileByFileContextPrefetchStage.StageIdConstant,
                    FileByFileRiskMarkerStage.StageIdConstant,
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

        var profiles = pipelineProfileProvider.GetProfiles();
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
                   [
                       FileByFileContextPrefetchStage.StageIdConstant,
                       FileByFileRiskMarkerStage.StageIdConstant,
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
        if (!protocolId.HasValue)
        {
            return;
        }

        // ProRV focused guidance now flows only through a prorv-lens pass; record its application when guidance is
        // present on the pass context (an ordinary/baseline pass carries none, so nothing is recorded there).
        var guidance = fileContext.PerFileHint?.FocusedReviewGuidance ?? [];
        if (guidance.Count == 0)
        {
            return;
        }

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
        if (fileContext.ContextBudgetOutcome != ReviewContextBudgetOutcome.Normal)
        {
            fileResult.MarkContextBudgetOutcome(fileContext.ContextBudgetOutcome);
        }

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

    private async Task<(IChatClient? tierClient, string? tierModelId, AgentReviewRuntimeCapabilities? tierCapabilities, int? tierMaxContextTokens, string?
        tierTokenizerName)> ResolveTierClientAsync(
        ReviewJob job,
        AiConnectionModelCategory tierCategory,
        AiPurpose tierPurpose,
        CancellationToken ct)
    {
        IChatClient? tierClient = null;
        string? tierModelId = null;
        AgentReviewRuntimeCapabilities? tierCapabilities = null;
        int? tierMaxContextTokens = null;
        string? tierTokenizerName = null;

        if (aiRuntimeResolver is not null)
        {
            try
            {
                var tierRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, tierPurpose, ct);
                tierClient = tierRuntime.ChatClient;
                tierModelId = tierRuntime.Model.RemoteModelId;
                tierCapabilities = tierRuntime.Capabilities;
                tierMaxContextTokens = tierRuntime.Model.MaxContextTokens;
                tierTokenizerName = tierRuntime.Model.TokenizerName;
            }
            catch
            {
                tierClient = null;
                tierModelId = null;
                tierCapabilities = null;
                tierMaxContextTokens = null;
                tierTokenizerName = null;
            }
        }
        else if (aiConnectionRepository is not null && aiClientFactory is not null)
        {
            var tierDto = await aiConnectionRepository.GetForTierAsync(job.ClientId, tierCategory, ct);
            if (tierDto is not null)
            {
                tierClient = aiClientFactory.CreateClient(tierDto.BaseUrl, tierDto.Secret);
                var boundModelId = tierDto.GetBoundModelId(tierPurpose);
                var tierModel = (boundModelId is not null
                                    ? tierDto.ConfiguredModels.FirstOrDefault(model => string.Equals(
                                        model.RemoteModelId, boundModelId, StringComparison.OrdinalIgnoreCase))
                                    : null)
                                ?? tierDto.ConfiguredModels.FirstOrDefault(model => model.SupportsChat);
                tierModelId = boundModelId ?? tierModel?.RemoteModelId;
                tierMaxContextTokens = tierModel?.MaxContextTokens;
                tierTokenizerName = tierModel?.TokenizerName;
            }
        }

        return (tierClient, tierModelId, tierCapabilities, tierMaxContextTokens, tierTokenizerName);
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
        string? Lens = null,
        bool Shadow = false,
        int? MaxContextTokens = null,
        string? TokenizerName = null,
        ReviewReasoningEffort ReasoningEffort = ReviewReasoningEffort.None);

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
        int? PassMaxContextTokens,
        string? PassTokenizerName,
        ReviewReasoningEffort PassReasoningEffort,
        CancellationToken Ct);
}
