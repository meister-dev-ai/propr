// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Synthesizes the final PR-level review result from completed per-file results. This executor gathers fresh
///     file summaries and comments, resolves the synthesis model/runtime, deduplicates the comments, applies the
///     quality filter only when the run skipped local verification, performs the synthesis and optional JSON repair
///     pass on the remaining findings, then combines synthesized cross-file findings with final-gate evaluation to
///     produce the final review summary and publishable comments.
/// </summary>
internal sealed class ReviewSynthesisExecutor(
    IReviewFileResultStore jobRepository,
    IProtocolRecorder protocolRecorder,
    ILogger<FileByFileReviewOrchestrator> logger,
    AiReviewOptions options,
    CandidateFindingFactory candidateFindingFactory,
    QualityFilterExecutor qualityFilterExecutor,
    PrLevelReviewVerificationExecutor? prLevelReviewVerificationExecutor,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    ISummaryReconciliationService? summaryReconciliationService,
    IAiRuntimeResolver? aiRuntimeResolver,
    IChatClient? defaultChatClient = null,
    IFindingDeduplicator? findingDeduplicator = null,
    IReviewFindingFinalizationPipeline? reviewFindingFinalizationPipeline = null,
    AcceptanceForecastExecutor? acceptanceForecastExecutor = null)
{
    private const int MaxRecordedMessageChars = 280;

    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    // Default deduplicator used whenever the client has not opted into multi-pass union: the existing
    // token-set Jaccard behavior, unchanged.
    private static readonly IFindingDeduplicator DefaultFindingDeduplicator = new TokenJaccardFindingDeduplicator();

    public async Task<ReviewResult> SynthesizeAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        IReadOnlyList<CandidateReviewFinding>? prWideCandidateFindings,
        FileReviewDispatchPlanner.BudgetSoftCapSummary budgetSoftCap,
        CancellationToken ct,
        bool fileFindingsJudged = false)
    {
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct);

        var allResults = jobWithResults!.FileReviewResults;

        var freshResults = allResults
            .Where(r => !r.IsCarriedForward)
            .ToList();

        var carriedForwardCandidatesSkipped = allResults
            .Where(r => r.IsComplete && r.IsCarriedForward && r.Comments is not null)
            .Sum(r => r.Comments!.Count);

        // Files that pre-flight context-window budgeting degraded to diff-only or skipped. Degraded files were
        // still reviewed and flow through synthesis normally; skipped files were never reviewed, so their
        // placeholder summary is kept out of the synthesis input and both are surfaced in the final summary.
        var contextDegradedFilePaths = freshResults
            .Where(r => r.ContextBudgetOutcome == ReviewContextBudgetOutcome.DegradedDiffOnly)
            .Select(r => r.FilePath)
            .ToList();
        var contextSkippedFilePaths = freshResults
            .Where(r => r.ContextBudgetOutcome == ReviewContextBudgetOutcome.Skipped)
            .Select(r => r.FilePath)
            .ToList();

        var perFileSummaries = freshResults
            .Where(r => r.IsComplete && r.PerFileSummary != null && r.ContextBudgetOutcome != ReviewContextBudgetOutcome.Skipped)
            .Select(r => (r.FilePath, Summary: r.PerFileSummary!))
            .ToList();

        // Shadow-pass comments stay in the persisted per-file result for the trace, but they are dropped here before
        // deduplication and gating so they never publish. Filtering before dedup means a finding a real (non-shadow)
        // pass independently produced still survives via its own comment — a shadow pass never suppresses a real one.
        var allComments = freshResults
            .Where(r => r.IsComplete && r.Comments is not null)
            .SelectMany(r => r.Comments!)
            .Where(comment => !comment.OriginPassShadow)
            .Select(NormalizeCommentAnchor)
            .ToList();

        var synthesisRuntime = await this.ResolveSynthesisRuntimeAsync(job, baseContext, effectiveClient, ct);

        var synthesisClient = synthesisRuntime.ChatClient;

        logger.LogInformation("Starting synthesis for job {JobId}", job.Id);

        var protocolId = await this.BeginSynthesisProtocolAsync(job, synthesisRuntime.ModelId, synthesisRuntime.LogicalModelName, ct);

        // Deduplication and the quality filter run before the synthesis call. The synthesis model then receives only
        // the findings that reach the gate, so its summary and its supporting finding ids cannot name a finding that
        // one of these steps removed.
        var deduped = await this.DeduplicateAsync(job, baseContext, allComments, protocolId, ct);
        deduped = await this.ApplyQualityFilterAsync(job, baseContext, effectiveClient, deduped, fileFindingsJudged, protocolId, ct);

        var changedLineRangesByPath = ReviewDiffProcessor.BuildChangedLineRangesByPath(pr.ChangedFiles);
        var baselineFindings = candidateFindingFactory.Build(freshResults, deduped, changedLineRangesByPath: changedLineRangesByPath);

        var synthesisOutcome = await this.RunSynthesisCoreAsync(
            job,
            pr,
            baseContext,
            perFileSummaries,
            deduped,
            baselineFindings,
            synthesisClient,
            synthesisRuntime.EffectiveModelId,
            protocolId,
            ct);

        var mergedPerFileFindings = CandidateFindingFactory.MergeFindings(baselineFindings, []);

        // Job-level PR-wide pass candidates join the synthesized cross-cutting findings and flow through the same
        // PR-level verification -> final gate -> publication path below. They are NOT semantically deduped against
        // per-file findings; they meet them only at the per-finding gate, matching how cross-cutting findings behave.
        var prWideFindings = prWideCandidateFindings ?? [];

        var gate = deterministicReviewFindingGate;
        if (gate is null)
        {
            IEnumerable<ReviewComment> synthesizedComments = synthesisOutcome.SynthesizedFindings.Count > 0
                ? CandidateFindingFactory.AssignSynthesisFindingIds(synthesisOutcome.SynthesizedFindings)
                    .Select(finding => FileByFileReviewOrchestrator.CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message)
                        with
                        {
                            OriginPassKind = finding.Provenance.ResolveOriginPassKindName(),
                            ScopeRelation = ReviewCommentScopeRelationMapper.Map(finding.ScopeRelation),
                        })
                : [];
            var prWideComments = prWideFindings
                .Select(finding => FileByFileReviewOrchestrator.CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message)
                    with
                    {
                        OriginPassKind = finding.Provenance.ResolveOriginPassKindName(),
                        OriginPassIndex = finding.Provenance.UnionPassIndex,
                        OriginPassLens = finding.Provenance.UnionLens,
                        ScopeRelation = ReviewCommentScopeRelationMapper.Map(finding.ScopeRelation),
                        OriginModelId = finding.Provenance.OriginModelId,
                        OriginLogicalModelName = finding.Provenance.OriginLogicalModelName,
                        OriginSymbolName = finding.Provenance.OriginSymbolName,
                        OriginSymbolKind = finding.Provenance.OriginSymbolKind,
                    });
            var combinedComments = synthesizedComments.Concat(prWideComments).Concat(deduped).ToList();

            logger.LogInformation(
                "Found {CrossCuttingCount} cross-cutting concerns in synthesis for job {JobId}",
                synthesisOutcome.SynthesizedFindings.Count,
                job.Id);
            return new ReviewResult(synthesisOutcome.FinalSummary, combinedComments)
            {
                CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
                ContextDegradedFilePaths = contextDegradedFilePaths,
                ContextSkippedFilePaths = contextSkippedFilePaths,
                BudgetSoftCapped = budgetSoftCap.SoftCapped,
                BudgetSoftCapThresholdUsd = budgetSoftCap.ThresholdUsd,
                BudgetSoftCapSpentUsd = budgetSoftCap.SpentUsd,
                BudgetSoftCapSkippedFilePaths = budgetSoftCap.SkippedFilePaths,
            };
        }

        var assignedSynthesisFindings = CandidateFindingFactory.AssignSynthesisFindingIds(synthesisOutcome.SynthesizedFindings);
        var synthesisAndPrWideFindings = prWideFindings.Count > 0
            ? assignedSynthesisFindings.Concat(prWideFindings).ToList()
            : assignedSynthesisFindings;
        var skipPrVerification = await this.TryRecordSkippedStepAsync(protocolId, baseContext, FileByFileReviewStepIds.PrVerification, ct);
        var prLevelFindings = prLevelReviewVerificationExecutor is null || skipPrVerification
            ? synthesisAndPrWideFindings
            : await prLevelReviewVerificationExecutor.ApplyAsync(
                synthesisAndPrWideFindings,
                baseContext,
                pr.SourceBranch,
                protocolId,
                defaultChatClient,
                ct,
                job.ClientId,
                new ReviewVerificationIntent(pr.Title, pr.Description, pr.LinkedItems ?? [], null, null),
                aiRuntimeResolver);

        var candidateFindings = mergedPerFileFindings
            .Concat(prLevelFindings)
            .ToList();

        var invariantFacts = reviewInvariantFactProviders?
                                 .SelectMany(provider => provider.GetFacts())
                                 .ToList()
                             ?? [];
        var skipFinalGate = await this.TryRecordSkippedStepAsync(protocolId, baseContext, FileByFileReviewStepIds.FinalGate, ct);
        var gateDecisions = skipFinalGate
            ? candidateFindings.Select(CreatePublishDecision).ToArray()
            : await gate.EvaluateAsync(candidateFindings, invariantFacts, ct);

        // Post-gate finalization checks (e.g. the reread-before-ERROR floor) refine the gate's decisions:
        // annotating, downgrading, or discarding findings that fail a check. Skipped when the base gate is
        // skipped (offline) so the offline path stays deterministic.
        if (!skipFinalGate && reviewFindingFinalizationPipeline is not null)
        {
            gateDecisions = await reviewFindingFinalizationPipeline.ApplyAsync(candidateFindings, gateDecisions, protocolId, ct).ConfigureAwait(false);
        }

        // Observe-only acceptance forecasting: one bounded model call that predicts the author's response per
        // publishable finding and records it as a protocol event. Runs after the gate and the finalization
        // checks so it sees the final publish set; changes nothing about the decisions or comments.
        if (options.EnableAcceptanceForecast && acceptanceForecastExecutor is not null)
        {
            await acceptanceForecastExecutor.RecordForecastsAsync(job, candidateFindings, gateDecisions, aiRuntimeResolver, protocolId, ct)
                .ConfigureAwait(false);
        }

        var reconciler = summaryReconciliationService ?? new SummaryReconciliationService();
        var skipSummaryReconciliation = await this.TryRecordSkippedStepAsync(protocolId, baseContext, FileByFileReviewStepIds.SummaryReconciliation, ct);
        var reconciliation = skipSummaryReconciliation
            ? new SummaryReconciliationResult(
                synthesisOutcome.FinalSummary,
                synthesisOutcome.FinalSummary,
                [],
                [],
                false,
                "skipped")
            : ReviewSummaryGrounding.Ground(
                reconciler.Reconcile(
                    synthesisOutcome.FinalSummary,
                    candidateFindings,
                    gateDecisions,
                    synthesisOutcome.SummaryFindingIds),
                candidateFindings,
                gateDecisions);

        if (protocolId.HasValue)
        {
            await this.RecordFinalGateProtocolAsync(protocolId.Value, candidateFindings, gateDecisions, reconciliation, ct);
            if (!skipSummaryReconciliation)
            {
                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.SummaryReconciliation,
                    JsonSerializer.Serialize(
                        new
                        {
                            rewritePerformed = reconciliation.RewritePerformed,
                            droppedCount = reconciliation.DroppedFindingIds.Count,
                            summaryOnlyCount = reconciliation.SummaryOnlyFindingIds.Count,
                        }),
                    JsonSerializer.Serialize(reconciliation, FinalGateJsonOptions),
                    null,
                    ct);
            }
        }

        var publishedComments = MaterializePublishedComments(candidateFindings, gateDecisions);

        logger.LogInformation(
            "Found {CrossCuttingCount} cross-cutting concerns in synthesis for job {JobId}",
            synthesisOutcome.SynthesizedFindings.Count,
            job.Id);
        return new ReviewResult(reconciliation.FinalSummary, publishedComments)
        {
            CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
            ContextDegradedFilePaths = contextDegradedFilePaths,
            ContextSkippedFilePaths = contextSkippedFilePaths,
            BudgetSoftCapped = budgetSoftCap.SoftCapped,
            BudgetSoftCapThresholdUsd = budgetSoftCap.ThresholdUsd,
            BudgetSoftCapSpentUsd = budgetSoftCap.SpentUsd,
            BudgetSoftCapSkippedFilePaths = budgetSoftCap.SkippedFilePaths,
        };
    }

    private async Task<SynthesisRuntimeSelection> ResolveSynthesisRuntimeAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        string? synthesisModelId = null;
        string? synthesisLogicalModelName = null;

        // The high-effort runtime is resolved, never constructed here. Two cases leave the selection unresolved:
        // a host composed without a runtime resolver, and a resolver with no high-effort binding. Synthesis then
        // runs on the client the orchestrator was built with, under the model id resolved below.
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var synthesisRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(
                    job.ClientId,
                    AiPurpose.ReviewHighEffort,
                    ct);
                effectiveClient = synthesisRuntime.ChatClient;
                synthesisModelId = synthesisRuntime.Model.RemoteModelId;
                synthesisLogicalModelName = synthesisRuntime.LogicalModelName;
            }
            catch
            {
                synthesisModelId = null;
                synthesisLogicalModelName = null;
            }
        }

        // The effective selection is returned rather than written onto baseContext. That context is shared with
        // every stage after synthesis, so a stage-specific model id left on it makes those stages run, and report
        // their token usage, against the synthesis model.
        var effectiveModelId = synthesisModelId
                               ?? baseContext.ModelId
                               ?? job.AiModel
                               ?? options.ModelId;

        return new SynthesisRuntimeSelection(effectiveClient, synthesisModelId, synthesisLogicalModelName, effectiveModelId);
    }


    private async Task<Guid?> BeginSynthesisProtocolAsync(
        ReviewJob job,
        string? synthesisModelId,
        string? synthesisLogicalModelName,
        CancellationToken ct)
    {
        try
        {
            return await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                "synthesis",
                null,
                AiConnectionModelCategory.HighEffort,
                synthesisModelId,
                ct,
                logicalModelName: synthesisLogicalModelName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to begin protocol for file {FilePath} in job {JobId}", "synthesis", job.Id);
            return null;
        }
    }

    private async Task<SynthesisExecutionOutcome> RunSynthesisCoreAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IReadOnlyList<(string FilePath, string Summary)> perFileSummaries,
        IReadOnlyList<ReviewComment> allComments,
        IReadOnlyList<CandidateReviewFinding> perFileCandidateFindings,
        IChatClient synthesisClient,
        string? synthesisModelId,
        Guid? protocolId,
        CancellationToken ct)
    {
        string finalSummary;
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings;
        IReadOnlyList<string> summaryFindingIds = [];
        string? synthesisInputSample = null;
        string? synthesisSystemPrompt = null;

        try
        {
            var expectsJson = allComments.Count > 0;
            var systemPrompt = ReviewPrompts.BuildSynthesisSystemPrompt(baseContext, expectsJson);
            synthesisSystemPrompt = systemPrompt;
            var userMessage = ReviewPrompts.BuildSynthesisUserMessage(
                perFileSummaries,
                pr.Title,
                pr.Description,
                allComments,
                perFileCandidateFindings,
                baseContext);
            synthesisInputSample = userMessage;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userMessage),
            };
            await PromptStageEvidenceRecorder.RecordAsync(baseContext, PromptStageKeys.SynthesisSystem, systemPrompt, null, ct);
            await PromptStageEvidenceRecorder.RecordAsync(baseContext, PromptStageKeys.SynthesisUser, null, userMessage, ct);

            var response = await synthesisClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = synthesisModelId, Temperature = baseContext.Temperature },
                ct);

            var responseText = response.Text ?? string.Empty;
            var usage = AiTokenUsageExtractor.FromResponse(response);
            var totalInputTokens = usage.InputTokens;
            var totalOutputTokens = usage.OutputTokens;
            var totalCachedInputTokens = usage.CachedInputTokens;
            var totalCacheWriteTokens = usage.CacheWriteTokens;
            var totalReasoningTokens = usage.ReasoningTokens;
            var aiCallCount = 1;
            var observedCacheUsage = !usage.IsEstimated;

            if (SynthesisResponseParser.TryParse(responseText, out var parsedSummary, out var parsedCrossCuttingFindings, out var parsedSummaryFindingIds))
            {
                finalSummary = parsedSummary;
                synthesizedFindings = parsedCrossCuttingFindings;
                summaryFindingIds = parsedSummaryFindingIds;
            }
            else if (expectsJson && SynthesisResponseParser.LooksLikeJsonObject(responseText))
            {
                logger.LogInformation("Attempting synthesis JSON repair for job {JobId}", job.Id);

                var repairMessages = new List<ChatMessage>(messages)
                {
                    new(ChatRole.Assistant, responseText),
                    new(ChatRole.User, BuildSynthesisJsonRepairPrompt()),
                };

                var repairResponse = await synthesisClient.GetResponseAsync(
                    repairMessages,
                    new ChatOptions { ModelId = synthesisModelId, Temperature = baseContext.Temperature },
                    ct);

                var repairUsage = AiTokenUsageExtractor.FromResponse(repairResponse);
                totalInputTokens += repairUsage.InputTokens;
                totalOutputTokens += repairUsage.OutputTokens;
                totalCachedInputTokens += repairUsage.CachedInputTokens;
                totalCacheWriteTokens += repairUsage.CacheWriteTokens;
                totalReasoningTokens += repairUsage.ReasoningTokens;
                aiCallCount++;
                observedCacheUsage |= !repairUsage.IsEstimated;

                var repairedText = repairResponse.Text ?? string.Empty;
                if (SynthesisResponseParser.TryParse(repairedText, out parsedSummary, out parsedCrossCuttingFindings, out parsedSummaryFindingIds))
                {
                    finalSummary = parsedSummary;
                    synthesizedFindings = parsedCrossCuttingFindings;
                    summaryFindingIds = parsedSummaryFindingIds;
                    logger.LogInformation("Synthesis JSON repair succeeded for job {JobId}", job.Id);
                }
                else
                {
                    finalSummary = BuildFallbackSummary(perFileSummaries);
                    synthesizedFindings = [];
                    logger.LogWarning("Synthesis JSON repair failed for job {JobId}; using fallback summary", job.Id);
                }
            }
            else
            {
                finalSummary = responseText;
                synthesizedFindings = [];
            }

            if (string.IsNullOrWhiteSpace(finalSummary))
            {
                finalSummary = BuildFallbackSummary(perFileSummaries);
            }

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    totalInputTokens,
                    totalOutputTokens,
                    userMessage,
                    systemPrompt,
                    finalSummary,
                    ct);

                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Completed",
                    totalInputTokens,
                    totalOutputTokens,
                    aiCallCount,
                    0,
                    null,
                    ct,
                    totalCachedInputTokens,
                    observedCacheUsage ? CacheObservabilityStatus.Observable : CacheObservabilityStatus.Unobservable,
                    totalCacheWriteTokens,
                    totalReasoningTokens);
            }

            logger.LogInformation("Completed synthesis for job {JobId}", job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Synthesis failed for job {JobId}; using fallback summary", job.Id);
            finalSummary = BuildFallbackSummary(perFileSummaries);
            synthesizedFindings = [];

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    0,
                    0,
                    synthesisInputSample,
                    synthesisSystemPrompt,
                    null,
                    ct,
                    "ai_call_synthesis_failed",
                    ex.Message);

                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }
        }

        return new SynthesisExecutionOutcome(finalSummary, synthesizedFindings, summaryFindingIds);
    }

    private async Task RecordFinalGateProtocolAsync(
        Guid protocolId,
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions,
        SummaryReconciliationResult reconciliation,
        CancellationToken ct)
    {
        var summary = RecordedFinalGateSummary.FromFindingsAndDecisions(findings, decisions, reconciliation);
        var summaryJson = JsonSerializer.Serialize(summary, FinalGateJsonOptions);
        var includedInFinalSummary = reconciliation.SummaryOnlyFindingIds.ToHashSet(StringComparer.Ordinal);

        await protocolRecorder.RecordReviewFindingGateEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReviewFindingGateSummary,
            summaryJson,
            summaryJson,
            null,
            ct);

        var findingsById = findings.ToDictionary(finding => finding.FindingId, StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!findingsById.TryGetValue(decision.FindingId, out var finding))
            {
                continue;
            }

            var recordedDecision = decision.ToRecordedDecision(
                finding,
                includedInFinalSummary.Contains(decision.FindingId));
            var details = JsonSerializer.Serialize(
                new
                {
                    decision.FindingId,
                    decision.Disposition,
                    decision.RuleSource,
                    decision.ReasonCodes,
                },
                FinalGateJsonOptions);
            var output = JsonSerializer.Serialize(recordedDecision, FinalGateJsonOptions);
            await protocolRecorder.RecordReviewFindingGateEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewFindingGateDecision,
                details,
                output,
                null,
                ct);
        }
    }

    // Selects the dedup strategy for this review. When the client opted into multi-pass union and a semantic
    // deduplicator is available, the unioned candidate set is collapsed semantically (same file + overlapping
    // anchor + same defect class); otherwise the exact token-Jaccard pipeline runs, so flag-off behavior is
    // byte-identical to before.
    // Every merge is recorded in the job protocol with the kept and the removed comments, so a comment that
    // deduplication removes after local verification can be traced to the comment that replaced it.
    private async Task<List<ReviewComment>> DeduplicateAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        IReadOnlyList<ReviewComment> allComments,
        Guid? protocolId,
        CancellationToken ct)
    {
        var semantic = baseContext.EnableMultiPassUnion && findingDeduplicator is not null;
        var deduplicator = semantic ? findingDeduplicator! : DefaultFindingDeduplicator;

        var deduplication = await deduplicator.DeduplicateAsync(allComments, job.ClientId, ct);
        await this.RecordCommentRemovalAsync(
            protocolId,
            ReviewProtocolEventNames.FindingDeduplication,
            semantic ? "semantic" : "token_similarity",
            allComments.Count,
            deduplication,
            ct);
        return deduplication.Comments.ToList();
    }

    // The quality filter judges comment text alone, without the code. When local verification decided the fresh file
    // findings with the evidence judge, which reads the code, the filter does not run. It runs when the run skipped
    // local verification or the file reviewer has no complete local verifier, so nothing judged the findings.
    private async Task<List<ReviewComment>> ApplyQualityFilterAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        List<ReviewComment> comments,
        bool fileFindingsJudged,
        Guid? protocolId,
        CancellationToken ct)
    {
        if (comments.Count < ResolveQualityFilterThreshold(job, options))
        {
            return comments;
        }

        if (fileFindingsJudged)
        {
            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordReviewStrategyEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.ReviewStepSkipped,
                    JsonSerializer.Serialize(new { stepId = FileByFileReviewStepIds.QualityFilter, scope = "synthesis" }),
                    JsonSerializer.Serialize(new { skipped = true, reason = "findings_decided_by_evidence_judge" }),
                    null,
                    ct);
            }

            return comments;
        }

        if (await this.TryRecordSkippedStepAsync(protocolId, baseContext, FileByFileReviewStepIds.QualityFilter, ct))
        {
            return comments;
        }

        var filtered = await qualityFilterExecutor.ApplyAsync(job.Id, comments, baseContext, effectiveClient, ct);
        await this.RecordCommentRemovalAsync(
            protocolId,
            ReviewProtocolEventNames.QualityFilterApplied,
            "quality_filter",
            comments.Count,
            // The filter rebuilds every comment it keeps, so input and output are matched by their content.
            FindingDeduplicationResult.FromDifference(comments, filtered, "quality_filter", CommentContentComparer.Instance),
            ct);
        return filtered;
    }

    private async Task RecordCommentRemovalAsync(
        Guid? protocolId,
        string eventName,
        string stage,
        int inputCount,
        FindingDeduplicationResult result,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var details = new
        {
            stage,
            inputCount,
            outputCount = result.Comments.Count,
            mergeCount = result.Merges.Count,
            removedCount = result.Merges.Sum(merge => merge.Removed.Count),
        };
        var output = new
        {
            details.stage,
            details.inputCount,
            details.outputCount,
            merges = result.Merges.Select(merge => new
            {
                merge.Reason,
                kept = merge.Kept.Select(DescribeComment).ToList(),
                removed = merge.Removed.Select(DescribeComment).ToList(),
            }).ToList(),
        };
        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            eventName,
            JsonSerializer.Serialize(details, FinalGateJsonOptions),
            JsonSerializer.Serialize(output, FinalGateJsonOptions),
            null,
            ct);
    }

    // The identity of a comment in the trace: its anchor, severity, producing pass and the head of its message.
    private static object DescribeComment(ReviewComment comment)
    {
        return new
        {
            comment.FilePath,
            comment.LineNumber,
            Severity = comment.Severity.ToString(),
            comment.OriginPassKind,
            comment.OriginPassIndex,
            comment.OriginPassLens,
            Message = comment.Message.Length <= MaxRecordedMessageChars ? comment.Message : comment.Message[..MaxRecordedMessageChars],
        };
    }

    internal static IReadOnlyList<ReviewComment> MaterializePublishedComments(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        var decisionsById = decisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        return candidateFindings
            .Where(finding => decisionsById.TryGetValue(finding.FindingId, out var decision)
                              && string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(finding => FileByFileReviewOrchestrator.CreateReviewComment(
                    finding.FilePath,
                    finding.LineNumber,
                    finding.Severity,
                    AppendPublicationNote(finding.Message, decisionsById[finding.FindingId].PublicationNote))
                with
                {
                    OriginPassKind = finding.Provenance.ResolveOriginPassKindName(),
                    OriginPassIndex = finding.Provenance.UnionPassIndex,
                    OriginPassLens = finding.Provenance.UnionLens,
                    ScopeRelation = ReviewCommentScopeRelationMapper.Map(finding.ScopeRelation),

                    // The model that produced the finding travels with the published comment, so a finding's
                    // durable record can attribute it without the collection path re-deriving anything.
                    OriginModelId = finding.Provenance.OriginModelId,
                    OriginLogicalModelName = finding.Provenance.OriginLogicalModelName,

                    // The definition the finding sits in travels with the published comment too, so the collected
                    // record can count findings per part of the codebase without re-parsing anything.
                    OriginSymbolName = finding.Provenance.OriginSymbolName,
                    OriginSymbolKind = finding.Provenance.OriginSymbolKind,
                })
            .ToList();
    }

    // Appends a finalization-check note (e.g. an unverified-ERROR notice) to the published comment body as a
    // trailing paragraph. Returns the message unchanged when no note applies.
    private static string AppendPublicationNote(string message, string? publicationNote)
    {
        return string.IsNullOrWhiteSpace(publicationNote) ? message : $"{message}\n\n{publicationNote}";
    }

    private async Task<bool> TryRecordSkippedStepAsync(
        Guid? protocolId,
        ReviewSystemContext baseContext,
        string stepId,
        CancellationToken ct)
    {
        if (!baseContext.SkippedSteps.Contains(stepId))
        {
            return false;
        }

        if (protocolId.HasValue)
        {
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.ReviewStepSkipped,
                JsonSerializer.Serialize(new { stepId, scope = "synthesis" }),
                JsonSerializer.Serialize(new { skipped = true }),
                null,
                ct);
        }

        return true;
    }

    /// <summary>
    ///     Resolves the effective quality-filter threshold for a job by consulting the profile catalog.
    ///     Profile overrides take precedence; falls back to the global <see cref="AiReviewOptions.QualityFilterThreshold" />.
    /// </summary>
    private static int ResolveQualityFilterThreshold(ReviewJob job, AiReviewOptions opts)
    {
        // Resolve via the profile catalog constants directly (no provider injection needed for a static lookup).
        int? profileOverride = job.ReviewPipelineProfileId switch
        {
            ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId => 1,
            ReviewPipelineProfileCatalog.FileByFileBalancedProfileId => 10,
            _ => null,
        };

        return profileOverride ?? opts.QualityFilterThreshold;
    }

    private static FinalGateDecision CreatePublishDecision(CandidateReviewFinding finding)
    {
        return new FinalGateDecision(
            finding.FindingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "offline_skip",
            [],
            finding.Evidence,
            null);
    }

    private static ReviewComment NormalizeCommentAnchor(ReviewComment comment)
    {
        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : FileByFileReviewOrchestrator.CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
    }

    private static string BuildFallbackSummary(IReadOnlyList<(string FilePath, string Summary)> perFileSummaries)
    {
        return string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
    }

    private static string BuildSynthesisJsonRepairPrompt()
    {
        return """
               Your previous response was not valid JSON.
               Reformat it now as a single raw JSON object with exactly these keys:
               - "summary": string
               - "cross_cutting_concerns": array of objects with keys "message", "severity", "category", "candidateSummaryText", "supportingFindingIds", "supportingFiles", "evidenceResolutionState", and "evidenceSource"

               Escape any quotes inside string values correctly.
               Do NOT use markdown fences.
               Do NOT add any prose before or after the JSON.
               The first character must be '{' and the last character must be '}'.
               """;
    }

    // ModelId is the synthesis-specific selection (null when none is bound), recorded as the protocol's model.
    // EffectiveModelId is what the synthesis call actually sends, after falling back to the job's or the
    // installation's model. It is deliberately not written onto the shared review context.
    private sealed record SynthesisRuntimeSelection(
        IChatClient ChatClient,
        string? ModelId,
        string? LogicalModelName,
        string? EffectiveModelId);

    private sealed record SynthesisExecutionOutcome(
        string FinalSummary,
        IReadOnlyList<CandidateReviewFinding> SynthesizedFindings,
        IReadOnlyList<string> SummaryFindingIds);

    // Matches two comments by anchor, severity and message.
    private sealed class CommentContentComparer : IEqualityComparer<ReviewComment>
    {
        public static readonly CommentContentComparer Instance = new();

        public bool Equals(ReviewComment? x, ReviewComment? y)
        {
            return ReferenceEquals(x, y)
                   || (x is not null && y is not null
                                     && string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal)
                                     && x.LineNumber == y.LineNumber
                                     && x.Severity == y.Severity
                                     && string.Equals(x.Message, y.Message, StringComparison.Ordinal));
        }

        public int GetHashCode(ReviewComment obj)
        {
            return HashCode.Combine(obj.FilePath, obj.LineNumber, obj.Severity, obj.Message);
        }
    }
}
