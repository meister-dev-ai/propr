// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed class AgenticReviewSynthesisExecutor(
    IJobRepository jobRepository,
    IProtocolRecorder protocolRecorder,
    ILogger<AgenticFileByFileReviewOrchestrator> logger,
    AiReviewOptions options,
    AgenticCandidateFindingFactory candidateFindingFactory,
    QualityFilterExecutor qualityFilterExecutor,
    PrLevelReviewVerificationExecutor? prLevelReviewVerificationExecutor,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders,
    ISummaryReconciliationService? summaryReconciliationService,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IAiRuntimeResolver? aiRuntimeResolver,
    IChatClient? defaultChatClient = null)
{
    private const string RepeatedJudgmentStage = "repeated_judgment";
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ReviewResult> SynthesizeAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        IReadOnlyList<CandidateReviewFinding>? agenticCandidateFindings,
        IReadOnlyList<CandidateReviewFinding>? augmentationCandidateFindings,
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

        var perFileSummaries = freshResults
            .Where(r => r.IsComplete && r.PerFileSummary != null)
            .Select(r => (r.FilePath, Summary: r.PerFileSummary!))
            .ToList();

        var allComments = freshResults
            .Where(r => r.IsComplete && r.Comments is not null)
            .SelectMany(r => r.Comments!)
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
            agenticCandidateFindings,
            perFileSummaries,
            allComments,
            effectiveClient,
            protocolId,
            ct);

        var deduped = FindingDeduplicator.Deduplicate(allComments).ToList();
        if (deduped.Count >= options.QualityFilterThreshold)
        {
            deduped = await qualityFilterExecutor.ApplyAsync(job.Id, deduped, baseContext, effectiveClient, ct);
        }

        var baselinePerFileFindings = candidateFindingFactory.Build(freshResults, deduped, agenticCandidateFindings);
        var perFileCandidateFindings = CandidateFindingFactory.MergeFindings(
            baselinePerFileFindings,
            augmentationCandidateFindings ?? []);
        await this.RecordLateSteeringMergeEventAsync(
            baseContext,
            baselinePerFileFindings.Count,
            augmentationCandidateFindings ?? [],
            perFileCandidateFindings,
            ct);

        var gate = deterministicReviewFindingGate;
        if (gate is null)
        {
            var combinedComments = synthesisOutcome.SynthesizedFindings.Count > 0
                ? AgenticCandidateFindingFactory.AssignSynthesisFindingIds(synthesisOutcome.SynthesizedFindings)
                    .Select(finding => AgenticFileByFileReviewOrchestrator.CreateReviewComment(
                        finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
                    .Concat(deduped)
                    .ToList()
                : (IReadOnlyList<ReviewComment>)deduped;

            logger.LogInformation(
                "Found {CrossCuttingCount} cross-cutting concerns in synthesis for job {JobId}",
                synthesisOutcome.SynthesizedFindings.Count,
                job.Id);
            return new ReviewResult(synthesisOutcome.FinalSummary, combinedComments)
            {
                CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
            };
        }

        var assignedSynthesisFindings = AgenticCandidateFindingFactory.AssignSynthesisFindingIds(synthesisOutcome.SynthesizedFindings);
        var prLevelFindings = prLevelReviewVerificationExecutor is null
            ? assignedSynthesisFindings
            : await prLevelReviewVerificationExecutor.ApplyAsync(
                assignedSynthesisFindings,
                baseContext,
                pr.SourceBranch,
                protocolId,
                defaultChatClient,
                ct);

        var perFileFindingsRequiringPrVerification = perFileCandidateFindings
            .Where(RequiresPrLevelVerification)
            .ToList();
        var verifiedPerFileFindings = prLevelReviewVerificationExecutor is null || perFileFindingsRequiringPrVerification.Count == 0
            ? perFileFindingsRequiringPrVerification
            : await prLevelReviewVerificationExecutor.ApplyAsync(
                perFileFindingsRequiringPrVerification,
                baseContext,
                pr.SourceBranch,
                protocolId,
                defaultChatClient,
                ct);
        verifiedPerFileFindings = await this.ApplyRepeatedJudgmentAsync(
            verifiedPerFileFindings,
            baseContext,
            pr.SourceBranch,
            protocolId,
            defaultChatClient,
            ct);

        var candidateFindings = perFileCandidateFindings
            .Where(finding => !RequiresPrLevelVerification(finding))
            .Concat(verifiedPerFileFindings)
            .Concat(prLevelFindings)
            .ToList();

        var invariantFacts = reviewInvariantFactProviders?
                                 .SelectMany(provider => provider.GetFacts())
                                 .ToList()
                             ?? [];
        var gateDecisions = await gate.EvaluateAsync(candidateFindings, invariantFacts, ct);
        var reconciler = summaryReconciliationService ?? new SummaryReconciliationService();
        var reconciliation = GroundSummaryToFinalGateOutcomes(
            reconciler.Reconcile(synthesisOutcome.FinalSummary, candidateFindings, gateDecisions),
            gateDecisions);

        if (protocolId.HasValue)
        {
            await this.RecordFinalGateProtocolAsync(protocolId.Value, candidateFindings, gateDecisions, reconciliation, ct);
            await this.RecordFollowUpDependencyEventsAsync(protocolId.Value, candidateFindings, gateDecisions, ct);
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

        var publishedComments = MaterializePublishedComments(candidateFindings, gateDecisions);

        logger.LogInformation(
            "Found {CrossCuttingCount} cross-cutting concerns in synthesis for job {JobId}",
            synthesisOutcome.SynthesizedFindings.Count,
            job.Id);
        return new ReviewResult(reconciliation.FinalSummary, publishedComments)
        {
            CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
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
                synthTierDto = synthesisRuntime.Connection;
                effectiveClient = synthesisRuntime.ChatClient;
                synthesisModelId = synthesisRuntime.Model.RemoteModelId;
            }
            catch
            {
                synthTierDto = null;
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

    private async Task RecordFollowUpDependencyEventsAsync(
        Guid protocolId,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> gateDecisions,
        CancellationToken ct)
    {
        var publishedOrSummaryOnlyFindingIds = gateDecisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                               || string.Equals(decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
            .Select(decision => decision.FindingId)
            .ToHashSet(StringComparer.Ordinal);
        if (publishedOrSummaryOnlyFindingIds.Count == 0)
        {
            return;
        }

        var dependentFindings = candidateFindings
            .Where(finding => publishedOrSummaryOnlyFindingIds.Contains(finding.FindingId)
                              && string.Equals(finding.Provenance.OriginKind, CandidateFindingProvenance.DeeperFollowUpOrigin, StringComparison.Ordinal)
                              && !string.IsNullOrWhiteSpace(finding.Provenance.SourceOriginId))
            .GroupBy(finding => new
            {
                TaskId = finding.Provenance.SourceOriginId!,
                finding.Provenance.SourceFilePath,
                finding.Provenance.EvidenceSetId,
            });

        foreach (var group in dependentFindings)
        {
            var firstFinding = group.First();
            var triggerFamily = ResolveTriggerFamily(firstFinding);
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.AgenticFileFollowUpDependencyRecorded,
                JsonSerializer.Serialize(
                    new
                    {
                        anchorFile = firstFinding.Provenance.SourceFilePath,
                        taskId = group.Key.TaskId,
                        triggerFamily,
                    }),
                JsonSerializer.Serialize(
                    new
                    {
                        dependencyRecorded = true,
                        evidenceSetId = group.Key.EvidenceSetId,
                        findingId = firstFinding.FindingId,
                        relatedFindingIds = group.Select(finding => finding.FindingId).ToList(),
                    }),
                null,
                ct);
        }
    }

    private static string ResolveTriggerFamily(CandidateReviewFinding finding)
    {
        if (finding.InvariantCheckContext.TryGetValue(CandidateReviewFinding.FollowUpTriggerFamilyContextKey, out var triggerFamily)
            && !string.IsNullOrWhiteSpace(triggerFamily))
        {
            return triggerFamily;
        }

        if (finding.InvariantCheckContext.TryGetValue(CandidateReviewFinding.ClaimKindContextKey, out var claimKind)
            && string.Equals(claimKind, CandidateReviewFinding.WiringMissingRegistrationClaimKind, StringComparison.Ordinal))
        {
            return "dispatch_or_registration";
        }

        return "explicit_follow_up_signal";
    }

    private async Task<List<CandidateReviewFinding>> ApplyRepeatedJudgmentAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        ReviewSystemContext reviewContext,
        string sourceBranch,
        Guid? protocolId,
        IChatClient? fallbackChatClient,
        CancellationToken ct)
    {
        if (prLevelReviewVerificationExecutor is null || findings.Count == 0)
        {
            return findings.ToList();
        }

        var updatedFindings = new List<CandidateReviewFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (!IsEligibleForRepeatedJudgment(finding))
            {
                updatedFindings.Add(finding);
                continue;
            }

            var repeatedJudgmentOutcome = await prLevelReviewVerificationExecutor.RunRepeatedJudgmentAsync(
                finding,
                reviewContext,
                sourceBranch,
                protocolId,
                fallbackChatClient,
                ct);
            if (repeatedJudgmentOutcome is null)
            {
                updatedFindings.Add(finding);
                continue;
            }

            updatedFindings.Add(ApplyRepeatedJudgmentOutcome(finding, repeatedJudgmentOutcome));
        }

        return updatedFindings;
    }

    private static bool IsEligibleForRepeatedJudgment(CandidateReviewFinding finding)
    {
        return string.Equals(finding.Provenance.OriginKind, CandidateFindingProvenance.DeeperFollowUpOrigin, StringComparison.Ordinal)
               && finding.Provenance.RequiresExplicitSupport
               && !string.IsNullOrWhiteSpace(finding.Provenance.EvidenceSetId)
               && finding.VerificationOutcome is not null
               && string.Equals(finding.VerificationOutcome.OutcomeKind, VerificationOutcome.UnresolvedKind, StringComparison.Ordinal)
               && string.Equals(finding.VerificationOutcome.RecommendedDisposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal)
               && !finding.VerificationOutcome.Degraded;
    }

    private static CandidateReviewFinding ApplyRepeatedJudgmentOutcome(
        CandidateReviewFinding finding,
        RepeatedJudgmentOutcome repeatedJudgment)
    {
        var agreementState = string.Equals(
            repeatedJudgment.VerificationOutcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
            ? "Agreed"
            : "Disagreed";

        var context = new Dictionary<string, string>(finding.InvariantCheckContext, StringComparer.Ordinal)
        {
            [CandidateReviewFinding.RepeatedJudgmentAgreementStateContextKey] = agreementState,
            [CandidateReviewFinding.RepeatedJudgmentUsedSameEvidenceSetContextKey] = bool.TrueString.ToLowerInvariant(),
            [CandidateReviewFinding.RepeatedJudgmentSupportSourceContextKey] = ClaimDescriptor.JudgmentAgreementSupportSource,
        };

        var provenance = new CandidateFindingProvenance(
            CandidateFindingProvenance.RepeatedJudgmentOrigin,
            RepeatedJudgmentStage,
            finding.Provenance.SourceFilePath,
            finding.Provenance.SourceFileResultId,
            finding.Provenance.SourceCommentOrdinal,
            finding.Provenance.EvidenceSetId,
            finding.Provenance.RequiresExplicitSupport,
            repeatedJudgment.SourceOriginId);

        return new CandidateReviewFinding(
            finding.FindingId,
            provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            context,
            repeatedJudgment.VerificationOutcome);
    }

    private async Task<SynthesisExecutionOutcome> RunSynthesisCoreAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IReadOnlyList<ReviewFileResult> freshResults,
        IReadOnlyList<CandidateReviewFinding>? agenticCandidateFindings,
        IReadOnlyList<(string FilePath, string Summary)> perFileSummaries,
        IReadOnlyList<ReviewComment> allComments,
        IChatClient effectiveClient,
        Guid? protocolId,
        CancellationToken ct)
    {
        string finalSummary;
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings = [];
        string? synthesisInputSample = null;
        string? synthesisSystemPrompt = null;

        try
        {
            var expectsJson = allComments.Count > 0;
            var perFileCandidateFindings = candidateFindingFactory.Build(freshResults, enrichedPerFileFindings: agenticCandidateFindings);
            var systemPrompt = ReviewPrompts.BuildSynthesisSystemPrompt(baseContext, expectsJson);
            synthesisSystemPrompt = systemPrompt;
            var userMessage = ReviewPrompts.BuildSynthesisUserMessage(
                perFileSummaries,
                pr.Title,
                pr.Description,
                allComments,
                perFileCandidateFindings);
            synthesisInputSample = userMessage;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userMessage),
            };

            var response = await effectiveClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = baseContext.ModelId, Temperature = baseContext.Temperature },
                ct);

            var responseText = response.Text ?? string.Empty;
            var totalInputTokens = response.Usage?.InputTokenCount ?? 0;
            var totalOutputTokens = response.Usage?.OutputTokenCount ?? 0;

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

                totalInputTokens += repairResponse.Usage?.InputTokenCount ?? 0;
                totalOutputTokens += repairResponse.Usage?.OutputTokenCount ?? 0;

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
                    1,
                    0,
                    null,
                    ct);
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

    private async Task RecordLateSteeringMergeEventAsync(
        ReviewSystemContext baseContext,
        int baselineCandidateCount,
        IReadOnlyList<CandidateReviewFinding> augmentationFindings,
        IReadOnlyList<CandidateReviewFinding> mergedPerFileFindings,
        CancellationToken ct)
    {
        if (baseContext.AugmentationMode != ReviewAugmentationMode.LateAugmentation ||
            !baseContext.ActiveProtocolId.HasValue ||
            baseContext.ProtocolRecorder is null)
        {
            return;
        }

        await baseContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            baseContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.LateSteeringMergeCompleted,
            JsonSerializer.Serialize(
                new
                {
                    baselineCandidateCount,
                    proRvCandidateCount = augmentationFindings.Count,
                    mergedCandidateCount = mergedPerFileFindings.Count,
                },
                FinalGateJsonOptions),
            JsonSerializer.Serialize(
                new
                {
                    baselineOnlyCount = mergedPerFileFindings.Count(finding => finding.Provenance.FindingProvenanceKind == FindingProvenanceKind.BaselineOnly),
                    proRvOnlyCount = mergedPerFileFindings.Count(finding => finding.Provenance.FindingProvenanceKind == FindingProvenanceKind.ProRVOnly),
                    bothCount = mergedPerFileFindings.Count(finding => finding.Provenance.FindingProvenanceKind == FindingProvenanceKind.Both),
                },
                FinalGateJsonOptions),
            null,
            ct);
    }

    private static IReadOnlyList<ReviewComment> MaterializePublishedComments(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        var decisionsById = decisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        return candidateFindings
            .Where(finding => decisionsById.TryGetValue(finding.FindingId, out var decision)
                              && string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(finding => AgenticFileByFileReviewOrchestrator.CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
    }

    private static SummaryReconciliationResult GroundSummaryToFinalGateOutcomes(
        SummaryReconciliationResult reconciliation,
        IReadOnlyList<FinalGateDecision> gateDecisions)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentNullException.ThrowIfNull(gateDecisions);

        var publishCount = gateDecisions.Count(decision =>
            string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
        var summaryOnlyItems = gateDecisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
            .Select(decision => decision.SummaryText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var groundedSummary = BuildGroundedSummary(reconciliation.FinalSummary, publishCount, summaryOnlyItems);
        if (string.Equals(reconciliation.FinalSummary, groundedSummary, StringComparison.Ordinal))
        {
            return reconciliation;
        }

        return new SummaryReconciliationResult(
            reconciliation.OriginalSummary,
            groundedSummary,
            reconciliation.DroppedFindingIds,
            reconciliation.SummaryOnlyFindingIds,
            true,
            "deterministic_summary_grounding");
    }

    private static string BuildGroundedSummary(string reconciledSummary, int publishCount, IReadOnlyList<string> summaryOnlyItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciledSummary);

        if (publishCount == 0 && summaryOnlyItems.Count == 0)
        {
            return "No publishable or summary-only findings remained after verification.";
        }

        var lines = new List<string>();
        var overview = ExtractGroundedOverview(reconciledSummary);
        if (!string.IsNullOrWhiteSpace(overview))
        {
            lines.Add(overview);
            lines.Add(string.Empty);
        }

        if (publishCount > 0)
        {
            lines.Add($"Verification retained {publishCount} publishable finding{(publishCount == 1 ? string.Empty : "s")}.");
        }

        if (summaryOnlyItems.Count > 0)
        {
            if (publishCount == 0)
            {
                lines.Add("No publishable findings remained after verification.");
            }

            lines.Add(string.Empty);
            lines.Add("Summary-only findings:");
            lines.AddRange(summaryOnlyItems.Select(item => $"- {item}"));
        }

        return string.Join(
            Environment.NewLine, lines.Where((line, index) => index == 0 || !(string.IsNullOrEmpty(line) && string.IsNullOrEmpty(lines[index - 1]))));
    }

    private static string? ExtractGroundedOverview(string summary)
    {
        var trimmed = summary.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var summaryOnlyIndex = trimmed.IndexOf("Summary-only findings:", StringComparison.Ordinal);
        if (summaryOnlyIndex >= 0)
        {
            trimmed = trimmed[..summaryOnlyIndex].TrimEnd();
        }

        if (!trimmed.StartsWith("This PR ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] safeLeadVerbs =
        [
            "This PR makes ",
            "This PR advances ",
            "This PR moves ",
            "This PR updates ",
            "This PR introduces ",
            "This PR adds ",
            "This PR broadens ",
            "This PR expands ",
            "This PR improves ",
            "This PR refactors ",
            "This PR reorganizes ",
            "This PR changes ",
        ];

        if (!safeLeadVerbs.Any(verb => trimmed.StartsWith(verb, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var contrastIndex = trimmed.IndexOf(", but ", StringComparison.OrdinalIgnoreCase);
        if (contrastIndex > 0)
        {
            return trimmed[..contrastIndex].TrimEnd().TrimEnd('.', ';', ',') + ".";
        }

        return null;
    }

    private static ReviewComment NormalizeCommentAnchor(ReviewComment comment)
    {
        var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : AgenticFileByFileReviewOrchestrator.CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
    }

    private static bool RequiresPrLevelVerification(CandidateReviewFinding finding)
    {
        return string.Equals(finding.Provenance.GeneratedByStage, "agentic_file_investigation", StringComparison.Ordinal)
               && (finding.Evidence?.SupportingFiles.Count > 1
                   || string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
                   || string.Equals(finding.Category, "architecture", StringComparison.Ordinal));
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
