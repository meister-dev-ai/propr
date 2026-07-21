// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterProPR.Application.AI;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Synthesizes the final PR-level review result from completed per-file results. This executor gathers fresh
///     file summaries and comments, resolves the synthesis model/runtime, performs the synthesis and optional JSON
///     repair pass, runs comment deduplication and optional quality filtering, then combines synthesized cross-file
///     findings with final-gate evaluation to produce the final review summary and publishable comments.
/// </summary>
internal sealed class ReviewSynthesisExecutor(
    IJobRepository jobRepository,
    IProtocolRecorder protocolRecorder,
    ILogger<FileByFileReviewOrchestrator> logger,
    AiReviewOptions options,
    CandidateFindingFactory candidateFindingFactory,
    QualityFilterExecutor qualityFilterExecutor,
    PrLevelReviewVerificationExecutor? prLevelReviewVerificationExecutor,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    ISummaryReconciliationService? summaryReconciliationService,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IAiRuntimeResolver? aiRuntimeResolver,
    IChatClient? defaultChatClient = null,
    IFindingDeduplicator? findingDeduplicator = null,
    IReviewFindingFinalizationPipeline? reviewFindingFinalizationPipeline = null)
{
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
        CancellationToken ct)
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

        effectiveClient = synthesisRuntime.ChatClient;

        logger.LogInformation("Starting synthesis for job {JobId}", job.Id);

        var protocolId = await this.BeginSynthesisProtocolAsync(job, synthesisRuntime.ModelId, ct);

        var synthesisOutcome = await this.RunSynthesisCoreAsync(
            job,
            pr,
            baseContext,
            freshResults,
            perFileSummaries,
            allComments,
            effectiveClient,
            protocolId,
            ct);

        var deduped = await this.DeduplicateAsync(job, baseContext, allComments, ct);
        var effectiveQualityFilterThreshold = ResolveQualityFilterThreshold(job, options);
        if (deduped.Count >= effectiveQualityFilterThreshold
            && !await this.TryRecordSkippedStepAsync(protocolId, baseContext, FileByFileReviewStepIds.QualityFilter, ct))
        {
            deduped = await qualityFilterExecutor.ApplyAsync(job.Id, deduped, baseContext, effectiveClient, ct);
        }

        var changedLineRangesByPath = ReviewDiffProcessor.BuildChangedLineRangesByPath(pr.ChangedFiles);
        var baselineFindings = candidateFindingFactory.Build(freshResults, deduped, changedLineRangesByPath: changedLineRangesByPath);
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
                ct);

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
                reconciler.Reconcile(synthesisOutcome.FinalSummary, candidateFindings, gateDecisions),
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
        AiConnectionDto? synthTierDto = null;
        string? synthesisModelId = null;

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
            }
            catch
            {
                synthesisModelId = null;
            }
        }
        else if (aiConnectionRepository is not null && aiClientFactory is not null)
        {
            synthTierDto = await aiConnectionRepository.GetForTierAsync(job.ClientId, AiConnectionModelCategory.HighEffort, ct);
            if (synthTierDto is not null)
            {
                effectiveClient = aiClientFactory.CreateClient(synthTierDto.BaseUrl, synthTierDto.Secret);
                synthesisModelId = synthTierDto.GetBoundModelId(AiPurpose.ReviewHighEffort)
                                   ?? synthTierDto.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
            }
        }

        baseContext.ModelId = synthesisModelId
                              ?? baseContext.ModelId
                              ?? job.AiModel
                              ?? options.ModelId;

        return new SynthesisRuntimeSelection(effectiveClient, synthesisModelId);
    }


    private async Task<Guid?> BeginSynthesisProtocolAsync(
        ReviewJob job,
        string? synthesisModelId,
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
                ct);
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
        IReadOnlyList<ReviewFileResult> freshResults,
        IReadOnlyList<(string FilePath, string Summary)> perFileSummaries,
        IReadOnlyList<ReviewComment> allComments,
        IChatClient effectiveClient,
        Guid? protocolId,
        CancellationToken ct)
    {
        string finalSummary;
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings;
        string? synthesisInputSample = null;
        string? synthesisSystemPrompt = null;

        try
        {
            var expectsJson = allComments.Count > 0;
            var perFileCandidateFindings = candidateFindingFactory.Build(freshResults);
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

            var response = await effectiveClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = baseContext.ModelId, Temperature = baseContext.Temperature },
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

            if (SynthesisResponseParser.TryParse(responseText, out var parsedSummary, out var parsedCrossCuttingFindings))
            {
                finalSummary = parsedSummary;
                synthesizedFindings = parsedCrossCuttingFindings;
            }
            else if (expectsJson && SynthesisResponseParser.LooksLikeJsonObject(responseText))
            {
                logger.LogInformation("Attempting synthesis JSON repair for job {JobId}", job.Id);

                var repairMessages = new List<ChatMessage>(messages)
                {
                    new(ChatRole.Assistant, responseText),
                    new(ChatRole.User, BuildSynthesisJsonRepairPrompt()),
                };

                var repairResponse = await effectiveClient.GetResponseAsync(
                    repairMessages,
                    new ChatOptions { ModelId = baseContext.ModelId, Temperature = baseContext.Temperature },
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
                if (SynthesisResponseParser.TryParse(repairedText, out parsedSummary, out parsedCrossCuttingFindings))
                {
                    finalSummary = parsedSummary;
                    synthesizedFindings = parsedCrossCuttingFindings;
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

        return new SynthesisExecutionOutcome(finalSummary, synthesizedFindings);
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
    private async Task<List<ReviewComment>> DeduplicateAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        IReadOnlyList<ReviewComment> allComments,
        CancellationToken ct)
    {
        var deduplicator = baseContext.EnableMultiPassUnion && findingDeduplicator is not null
            ? findingDeduplicator
            : DefaultFindingDeduplicator;

        var deduped = await deduplicator.DeduplicateAsync(allComments, job.ClientId, ct);
        return deduped.ToList();
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

    private sealed record SynthesisRuntimeSelection(IChatClient ChatClient, string? ModelId);

    private sealed record SynthesisExecutionOutcome(string FinalSummary, IReadOnlyList<CandidateReviewFinding> SynthesizedFindings);
}
