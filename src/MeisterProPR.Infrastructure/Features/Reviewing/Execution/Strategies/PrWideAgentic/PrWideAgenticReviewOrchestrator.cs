// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;

/// <summary>
///     Runs the staged PR-wide review strategy. Stage A/B/C always execute natively. When the shared
///     verification/final-gate services are available, Stage D/E also execute natively; otherwise the
///     orchestrator falls back to the legacy delegated file-by-file result path.
/// </summary>
public sealed partial class PrWideAgenticReviewOrchestrator(
    IFileByFileReviewOrchestrator fileByFileReviewOrchestrator,
    IOptions<AiReviewOptions> options,
    ILogger<PrWideAgenticReviewOrchestrator> logger,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate = null,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null,
    IReviewClaimExtractor? reviewClaimExtractor = null,
    IReviewEvidenceCollector? reviewEvidenceCollector = null,
    ISummaryReconciliationService? summaryReconciliationService = null,
    IReviewFindingVerifier? reviewFindingVerifier = null)
    : IPrWideAgenticReviewOrchestrator
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeterministicReviewFindingGate? _deterministicReviewFindingGate = deterministicReviewFindingGate;
    private readonly ILogger<PrWideAgenticReviewOrchestrator> _logger = logger;

    private readonly AiReviewOptions _options = options.Value;
    private readonly IReviewClaimExtractor? _reviewClaimExtractor = reviewClaimExtractor;
    private readonly IReviewEvidenceCollector? _reviewEvidenceCollector = reviewEvidenceCollector;
    private readonly IReviewFindingVerifier _reviewFindingVerifier = reviewFindingVerifier ?? new DeterministicLocalReviewVerifier();

    private readonly IReadOnlyList<InvariantFact> _reviewInvariantFacts = reviewInvariantFactProviders?
                                                                              .SelectMany(provider => provider.GetFacts())
                                                                              .ToList()
                                                                          ?? [];

    private readonly ISummaryReconciliationService? _summaryReconciliationService = summaryReconciliationService;

    /// <inheritdoc />
    public async Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null)
    {
        var prWideContext = baseContext;
        var ownsProtocolPass = false;
        var totalInputTokens = 0L;
        var totalOutputTokens = 0L;
        var totalAiCalls = 0;
        var totalToolCalls = 0;

        if (!prWideContext.ActiveProtocolId.HasValue && prWideContext.ProtocolRecorder is not null)
        {
            try
            {
                var protocolId = await prWideContext.ProtocolRecorder.BeginAsync(
                    job.Id,
                    job.RetryCount + 1,
                    "pr-wide-review",
                    null,
                    AiConnectionModelCategory.HighEffort,
                    overrideClient is not null ? prWideContext.ModelId : prWideContext.DefaultReviewModelId ?? prWideContext.ModelId,
                    ct);

                prWideContext = CloneContext(prWideContext, protocolId, prWideContext.ProtocolRecorder);
                ownsProtocolPass = true;
            }
            catch (Exception ex)
            {
                LogProtocolBeginFailed(this._logger, job.Id, ex);
            }
        }

        try
        {
            var (plan, planInputTokens, planOutputTokens, planAiCalls) = await this.CreatePlanAsync(job, pr, prWideContext, overrideClient, ct);
            totalInputTokens += planInputTokens;
            totalOutputTokens += planOutputTokens;
            totalAiCalls += planAiCalls;

            var (investigations, investigationInputTokens, investigationOutputTokens, investigationAiCalls, investigationToolCalls) =
                await this.RunInvestigationsAsync(job, pr, prWideContext, plan, overrideClient, ct);
            totalInputTokens += investigationInputTokens;
            totalOutputTokens += investigationOutputTokens;
            totalAiCalls += investigationAiCalls;
            totalToolCalls += investigationToolCalls;

            var (synthesis, synthesisInputTokens, synthesisOutputTokens, synthesisAiCalls) =
                await this.RecordSynthesisAsync(job, prWideContext, plan, investigations, overrideClient, ct);
            totalInputTokens += synthesisInputTokens;
            totalOutputTokens += synthesisOutputTokens;
            totalAiCalls += synthesisAiCalls;

            ReviewResult result;
            if (this.CanRunNativeVerification())
            {
                result = await this.BuildNativeReviewResultAsync(job, pr, prWideContext, synthesis, ct);
            }
            else
            {
                result = await fileByFileReviewOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient);
                await this.RecordDelegatedCompletionAsync(job, prWideContext, result, ct);
            }

            if (ownsProtocolPass && prWideContext.ActiveProtocolId.HasValue && prWideContext.ProtocolRecorder is not null)
            {
                await prWideContext.ProtocolRecorder.SetCompletedAsync(
                    prWideContext.ActiveProtocolId.Value,
                    "Completed",
                    totalInputTokens,
                    totalOutputTokens,
                    totalAiCalls,
                    totalToolCalls,
                    null,
                    ct);
            }

            return result;
        }
        catch
        {
            if (ownsProtocolPass && prWideContext.ActiveProtocolId.HasValue && prWideContext.ProtocolRecorder is not null)
            {
                await prWideContext.ProtocolRecorder.SetCompletedAsync(
                    prWideContext.ActiveProtocolId.Value,
                    "Failed",
                    totalInputTokens,
                    totalOutputTokens,
                    totalAiCalls,
                    totalToolCalls,
                    null,
                    ct);
            }

            throw;
        }
    }

    private async Task<(PrWideReviewPlan Plan, long InputTokens, long OutputTokens, int AiCalls)> CreatePlanAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient? overrideClient,
        CancellationToken ct)
    {
        var effectiveClient = overrideClient ?? baseContext.DefaultReviewChatClient ?? baseContext.TierChatClient;
        if (effectiveClient is not null)
        {
            try
            {
                var messages = new[]
                {
                    new ChatMessage(ChatRole.System, ReviewPrompts.BuildPrWidePlanningSystemPrompt(baseContext)),
                    new ChatMessage(ChatRole.User, ReviewPrompts.BuildPrWidePlanningUserMessage(pr)),
                };
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_planning_system", messages[0].Text, null, ct);
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_planning_user", null, messages[1].Text, ct);
                var response = await effectiveClient.GetResponseAsync(messages, new ChatOptions { ModelId = baseContext.ModelId }, ct);
                await this.RecordAiResponseAsync(baseContext, 1, messages, response, "pr_wide_planning", ct);
                var inputTokens = response.Usage?.InputTokenCount ?? 0;
                var outputTokens = response.Usage?.OutputTokenCount ?? 0;

                if (PrWideArtifactParser.TryParsePlan(response.Text, out var parsedPlan) && parsedPlan is not null)
                {
                    return (await this.RecordPlanAsync(job, baseContext, parsedPlan, ct), inputTokens, outputTokens, 1);
                }
            }
            catch (Exception ex)
            {
                LogStageFallback(this._logger, "planning", job.Id, ex.Message);
            }
        }

        var manifest = pr.AllPrFileSummaries;
        var prioritizedFiles = manifest
            .OrderByDescending(summary => ScoreFilePriority(summary.Path, pr))
            .ThenBy(summary => summary.Path, StringComparer.Ordinal)
            .ToList();

        var taskCount = Math.Min(2, prioritizedFiles.Count);
        var tasks = prioritizedFiles
            .Take(taskCount)
            .Select((summary, index) =>
            {
                var concern = BuildConcern(summary.Path);
                return new PrWideInvestigationTask(
                    $"task-{index + 1:D3}",
                    "concern",
                    concern,
                    [summary.Path],
                    [BoundedReviewContextTools.GetFileContentToolName, BoundedReviewContextTools.GetFileTreeToolName],
                    1);
            })
            .ToList();

        var plan = new PrWideReviewPlan(
            "plan-001",
            tasks.Select(task => task.Concern).ToList(),
            prioritizedFiles.Select(summary => summary.Path).ToList(),
            tasks,
            tasks.Count == 0 ? "No risky changed areas required deeper investigation." : null);

        return (await this.RecordPlanAsync(job, baseContext, plan, ct), 0, 0, 0);
    }

    private async Task<(IReadOnlyList<PrWideInvestigationResult> Results, long InputTokens, long OutputTokens, int AiCalls, int ToolCalls)>
        RunInvestigationsAsync(
            ReviewJob job,
            PullRequest pr,
            ReviewSystemContext baseContext,
            PrWideReviewPlan plan,
            IChatClient? overrideClient,
            CancellationToken ct)
    {
        if (plan.InvestigationTasks.Count == 0)
        {
            return ([], 0, 0, 0, 0);
        }

        var results = new List<PrWideInvestigationResult>(plan.InvestigationTasks.Count);
        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        var totalAiCalls = 0;
        var totalToolCalls = 0;
        foreach (var task in plan.InvestigationTasks)
        {
            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideInvestigationLaunched,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "investigation",
                    jobId = job.Id,
                    taskId = task.Id,
                    taskType = task.TaskType,
                    concern = task.Concern,
                },
                new
                {
                    allowedTools = task.AllowedTools,
                    budget = new { maxToolCalls = task.MaxToolCalls },
                    scope = task.SeedFilePaths,
                },
                ct);

            var (investigation, inputTokens, outputTokens, aiCalls) =
                await this.RunSingleInvestigationAsync(pr, baseContext, plan, task, overrideClient, ct);
            results.Add(investigation);
            totalInputTokens += inputTokens;
            totalOutputTokens += outputTokens;
            totalAiCalls += aiCalls;
            totalToolCalls += investigation.ToolUsage.Count(usage => string.Equals(
                usage.Status, BoundedReviewContextTools.SuccessStatus, StringComparison.Ordinal));

            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideInvestigationResult,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "investigation",
                    jobId = job.Id,
                    taskId = investigation.TaskId,
                    status = investigation.Status,
                },
                new
                {
                    evidenceCount = investigation.Evidence.Count,
                    candidateIssueCount = investigation.CandidateFindings.Count,
                    toolUsage = investigation.ToolUsage,
                    degraded = investigation.Degraded,
                },
                ct);
        }

        return (results, totalInputTokens, totalOutputTokens, totalAiCalls, totalToolCalls);
    }

    private async Task<(PrWideInvestigationResult Result, long InputTokens, long OutputTokens, int AiCalls)> RunSingleInvestigationAsync(
        PullRequest pr,
        ReviewSystemContext baseContext,
        PrWideReviewPlan plan,
        PrWideInvestigationTask task,
        IChatClient? overrideClient,
        CancellationToken ct)
    {
        var reviewTools = baseContext.ReviewTools;
        if (reviewTools is null)
        {
            return (new PrWideInvestigationResult(task.Id, "degraded", [], [], [], true), 0, 0, 0);
        }

        var boundedTools = new BoundedReviewContextTools(
            reviewTools,
            task.AllowedTools,
            task.MaxToolCalls,
            task.SeedFilePaths);

        var evidence = new List<EvidenceItem>();
        foreach (var filePath in task.SeedFilePaths)
        {
            var content = await boundedTools.GetFileContentAsync(filePath, pr.SourceBranch, 1, this._options.FileBatchLines, ct);
            if (!string.IsNullOrWhiteSpace(content))
            {
                evidence.Add(new EvidenceItem("file_content", $"Captured bounded context for {filePath}", filePath));
            }
        }

        if (evidence.Count > 0)
        {
            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideEvidenceCollected,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "investigation",
                    taskId = task.Id,
                },
                new
                {
                    evidenceCount = evidence.Count,
                    evidence = evidence.Select(item => new { item.Kind, item.SourceId, item.Summary }).ToList(),
                    toolUsage = boundedTools.Attempts,
                },
                ct);
        }

        var effectiveClient = overrideClient ?? baseContext.DefaultReviewChatClient ?? baseContext.TierChatClient;
        if (effectiveClient is not null)
        {
            try
            {
                var messages = new[]
                {
                    new ChatMessage(ChatRole.System, ReviewPrompts.BuildPrWideInvestigationSystemPrompt(baseContext)),
                    new ChatMessage(ChatRole.User, ReviewPrompts.BuildPrWideInvestigationUserMessage(plan, task, pr)),
                };
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_investigation_system", messages[0].Text, null, ct);
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_investigation_user", null, messages[1].Text, ct);
                var response = await effectiveClient.GetResponseAsync(messages, new ChatOptions { ModelId = baseContext.ModelId }, ct);
                await this.RecordAiResponseAsync(baseContext, 1, messages, response, $"pr_wide_investigation_{task.Id}", ct);
                var inputTokens = response.Usage?.InputTokenCount ?? 0;
                var outputTokens = response.Usage?.OutputTokenCount ?? 0;

                if (PrWideArtifactParser.TryParseInvestigationResult(response.Text, task, boundedTools.Attempts.ToList(), out var parsed) && parsed is not null)
                {
                    return (parsed, inputTokens, outputTokens, 1);
                }
            }
            catch (Exception ex)
            {
                LogStageFallback(this._logger, "investigation", Guid.Empty, ex.Message);
            }
        }

        IReadOnlyList<PrWideCandidateFinding> candidateFindings = evidence.Count == 0
            ? []
            :
            [
                new PrWideCandidateFinding(
                    $"candidate-{task.Id}",
                    $"Investigate cross-file concern: {task.Concern}",
                    CandidateReviewFinding.CrossCuttingCategory,
                    new ConfidenceScore("cross_file_reasoning", 70),
                    new EvidenceReference([], task.SeedFilePaths, EvidenceReference.ResolvedState, "pr_wide_investigation"),
                    task.SeedFilePaths),
            ];

        return (
            new PrWideInvestigationResult(
                task.Id,
                "completed",
                evidence,
                candidateFindings,
                boundedTools.Attempts.ToList(),
                false),
            0,
            0,
            0);
    }

    private async Task<(PrWideSynthesisResult Synthesis, long InputTokens, long OutputTokens, int AiCalls)> RecordSynthesisAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        PrWideReviewPlan plan,
        IReadOnlyList<PrWideInvestigationResult> investigations,
        IChatClient? overrideClient,
        CancellationToken ct)
    {
        var (synthesis, inputTokens, outputTokens, aiCalls) = await this.SynthesizeCandidatesAsync(plan, investigations, baseContext, overrideClient, ct);
        var candidateCount = synthesis.CandidateFindings.Count;

        foreach (var candidate in synthesis.CandidateFindings)
        {
            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideCandidateMerged,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "synthesis",
                    jobId = job.Id,
                    findingId = candidate.Id,
                },
                new
                {
                    canonicalFindingId = candidate.Id,
                    mergedSourceIds = investigations
                        .Where(result => result.CandidateFindings.Any(finding => finding.Id == candidate.Id))
                        .Select(result => result.TaskId)
                        .ToList(),
                    mergeReason = "central_synthesis",
                    preservedProvenance = candidate.RelatedFilePaths,
                },
                ct);
        }

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWideSynthesisCompleted,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "synthesis",
                jobId = job.Id,
                planId = plan.PlanId,
            },
            new
            {
                candidateCount,
                mergedDuplicateCount = Math.Max(0, investigations.Sum(result => result.CandidateFindings.Count) - synthesis.CandidateFindings.Count),
                conflictCount = 0,
                rankedCandidateIds = synthesis.CandidateFindings
                    .Select(candidate => candidate.Id)
                    .ToList(),
                stageMetrics = new
                {
                    investigationCount = investigations.Count,
                    degradedCount = investigations.Count(result => result.Degraded),
                },
            },
            ct);

        LogArtifactsRecorded(this._logger, job.Id);
        return (synthesis, inputTokens, outputTokens, aiCalls);
    }

    private async Task RecordDelegatedCompletionAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        ReviewResult result,
        CancellationToken ct)
    {
        var publishableCommentCount = result.Comments.Count;
        var inlineCommentCount = result.Comments.Count(IsInlineComment);
        var prLevelCommentCount = publishableCommentCount - inlineCommentCount;
        var severityCounts = result.Comments
            .GroupBy(comment => comment.Severity.ToString(), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "verification",
                jobId = job.Id,
                delegatedTo = "file_by_file",
                status = "completed",
            },
            new
            {
                delegatedTo = "file_by_file",
                publishableCommentCount,
                severityCounts,
                carriedForwardFileCount = result.CarriedForwardFilePaths.Count,
            },
            ct);

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWideFinalGateDecision,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "final_gate",
                jobId = job.Id,
                delegatedTo = "file_by_file",
                status = "completed",
            },
            new
            {
                delegatedTo = "file_by_file",
                publishableCommentCount,
                inlineCommentCount,
                prLevelCommentCount,
            },
            ct);

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWideSummaryReconciled,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "summary_reconciliation",
                jobId = job.Id,
                delegatedTo = "file_by_file",
                status = "completed",
            },
            new
            {
                delegatedTo = "file_by_file",
                summaryPresent = !string.IsNullOrWhiteSpace(result.Summary),
                summaryLength = result.Summary.Length,
                summaryPreview = CreateSummaryPreview(result.Summary),
                carriedForwardFileCount = result.CarriedForwardFilePaths.Count,
            },
            ct);

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "publication_preparation",
                jobId = job.Id,
                delegatedTo = "file_by_file",
                status = "completed",
            },
            new
            {
                delegatedTo = "file_by_file",
                requestedPublicationMode = job.ReviewPublicationMode.ToString(),
                comparisonMode = job.ReviewComparisonMode.ToString(),
                publishableCommentCount,
                inlineCommentCount,
                prLevelCommentCount,
                summaryOnlyCount = 0,
            },
            ct);
    }

    private async Task RecordNativeCompletionAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> gateDecisions,
        SummaryReconciliationResult reconciliation,
        ReviewResult result,
        CancellationToken ct)
    {
        var decisionsByFindingId = gateDecisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        foreach (var finding in candidateFindings)
        {
            if (!decisionsByFindingId.TryGetValue(finding.FindingId, out var decision))
            {
                continue;
            }

            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideVerificationCompleted,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "verification",
                    jobId = job.Id,
                    findingId = finding.FindingId,
                    claimOutcome = finding.VerificationOutcome?.OutcomeKind ?? VerificationOutcome.NotApplicableKind,
                },
                new
                {
                    findingId = finding.FindingId,
                    claimId = finding.VerificationOutcome?.ClaimId,
                    recommendedDisposition = finding.VerificationOutcome?.RecommendedDisposition ?? decision.Disposition,
                    outcomeKind = finding.VerificationOutcome?.OutcomeKind ?? VerificationOutcome.NotApplicableKind,
                    reasonCodes = finding.VerificationOutcome?.ReasonCodes ?? decision.ReasonCodes,
                    evidenceStrength = finding.VerificationOutcome?.EvidenceStrength,
                    degraded = finding.VerificationOutcome?.Degraded ?? false,
                },
                ct);

            await this.RecordStageEventAsync(
                baseContext,
                ReviewProtocolEventNames.PrWideFinalGateDecision,
                new
                {
                    strategy = "pr_wide_agentic",
                    stage = "final_gate",
                    jobId = job.Id,
                    findingId = finding.FindingId,
                },
                new
                {
                    findingId = finding.FindingId,
                    finalDisposition = decision.Disposition,
                    reasonCodes = decision.ReasonCodes,
                    duplicateState = "not_evaluated",
                    includedInFinalSummary = reconciliation.SummaryOnlyFindingIds.Contains(finding.FindingId, StringComparer.Ordinal),
                },
                ct);
        }

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWideSummaryReconciled,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "summary_reconciliation",
                jobId = job.Id,
            },
            new
            {
                removedDroppedFindingIds = reconciliation.DroppedFindingIds,
                retainedSummaryOnlyCount = reconciliation.SummaryOnlyFindingIds.Count,
                rewritePerformed = reconciliation.RewritePerformed,
                summaryRuleSource = reconciliation.RuleSource,
                finalSummaryDigest = CreateSummaryPreview(reconciliation.FinalSummary),
            },
            ct);

        var publishableCommentCount = result.Comments.Count;
        var inlineCommentCount = result.Comments.Count(IsInlineComment);
        var prLevelCommentCount = publishableCommentCount - inlineCommentCount;
        var summaryOnlyCount = gateDecisions.Count(decision => string.Equals(
            decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal));
        var droppedCount = gateDecisions.Count(decision => string.Equals(decision.Disposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal));

        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "publication_preparation",
                jobId = job.Id,
            },
            new
            {
                requestedPublicationMode = job.ReviewPublicationMode.ToString(),
                comparisonMode = job.ReviewComparisonMode.ToString(),
                publishableCommentCount,
                publishableInlineCount = inlineCommentCount,
                publishablePrLevelCount = prLevelCommentCount,
                summaryOnlyCount,
                droppedCount,
            },
            ct);
    }

    private async Task<(PrWideSynthesisResult Synthesis, long InputTokens, long OutputTokens, int AiCalls)> SynthesizeCandidatesAsync(
        PrWideReviewPlan plan,
        IReadOnlyList<PrWideInvestigationResult> investigations,
        ReviewSystemContext baseContext,
        IChatClient? overrideClient,
        CancellationToken ct)
    {
        var effectiveClient = overrideClient ?? baseContext.DefaultReviewChatClient ?? baseContext.TierChatClient;
        if (effectiveClient is not null)
        {
            try
            {
                var messages = new[]
                {
                    new ChatMessage(ChatRole.System, ReviewPrompts.BuildPrWideSynthesisSystemPrompt(baseContext)),
                    new ChatMessage(ChatRole.User, ReviewPrompts.BuildPrWideSynthesisUserMessage(plan, investigations)),
                };
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_synthesis_system", messages[0].Text, null, ct);
                await PromptStageEvidenceRecorder.RecordAsync(baseContext, "pr_wide_synthesis_user", null, messages[1].Text, ct);
                var response = await effectiveClient.GetResponseAsync(messages, new ChatOptions { ModelId = baseContext.ModelId }, ct);
                await this.RecordAiResponseAsync(baseContext, 1, messages, response, "pr_wide_synthesis", ct);
                var inputTokens = response.Usage?.InputTokenCount ?? 0;
                var outputTokens = response.Usage?.OutputTokenCount ?? 0;

                if (PrWideArtifactParser.TryParseSynthesisResult(response.Text, out var parsed) &&
                    parsed is not null &&
                    parsed.CandidateFindings.Count > 0)
                {
                    return (parsed, inputTokens, outputTokens, 1);
                }
            }
            catch (Exception ex)
            {
                LogStageFallback(this._logger, "synthesis", Guid.Empty, ex.Message);
            }
        }

        var candidates = investigations
            .SelectMany(result => result.CandidateFindings)
            .GroupBy(finding => finding.Message, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Confidence.Score).First())
            .ToList();

        return (
            new PrWideSynthesisResult(
                candidates.Count == 0
                    ? "No PR-wide candidate findings were synthesized."
                    : $"PR-wide synthesis produced {candidates.Count} candidate finding(s).",
                candidates),
            0,
            0,
            0);
    }

    private async Task RecordAiResponseAsync(
        ReviewSystemContext context,
        int iteration,
        IReadOnlyList<ChatMessage> messages,
        ChatResponse response,
        string eventName,
        CancellationToken ct)
    {
        if (!context.ActiveProtocolId.HasValue || context.ProtocolRecorder is null)
        {
            return;
        }

        var inputSample = GetInputSample(messages);
        var systemPrompt = GetSystemPrompt(messages);
        var outputSample = response.Text ?? response.Messages.LastOrDefault()?.Text;

        await context.ProtocolRecorder.RecordAiCallAsync(
            context.ActiveProtocolId.Value,
            iteration,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            inputSample,
            systemPrompt,
            outputSample,
            ct,
            eventName);
    }

    private static string? GetInputSample(IReadOnlyList<ChatMessage> messages)
    {
        return string.Join("\n\n", messages.SelectMany(message => message.Contents).Select(RenderContent).Where(text => !string.IsNullOrWhiteSpace(text)))
            is { Length: > 0 } combined
            ? combined.Length <= 4000 ? combined : combined[..4000]
            : null;
    }

    private static string? GetSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var prompt = string.Join(
            "\n\n",
            messages.Where(message => message.Role == ChatRole.System).SelectMany(message => message.Contents).Select(RenderContent)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        return prompt.Length == 0 ? null : prompt.Length <= 4000 ? prompt : prompt[..4000];
    }

    private static string? RenderContent(AIContent content)
    {
        return content switch
        {
            TextContent text => text.Text,
            DataContent data => data.Uri,
            _ => content.ToString(),
        };
    }

    private bool CanRunNativeVerification()
    {
        return this._deterministicReviewFindingGate is not null
               && this._reviewClaimExtractor is not null
               && this._reviewEvidenceCollector is not null
               && this._reviewFindingVerifier is not null
               && this._summaryReconciliationService is not null;
    }

    private async Task<ReviewResult> BuildNativeReviewResultAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        PrWideSynthesisResult synthesis,
        CancellationToken ct)
    {
        var candidateFindings = synthesis.CandidateFindings
            .Select(candidate => candidate.ToCandidateReviewFinding(
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "pr_wide_synthesis")))
            .ToList();

        candidateFindings = await this.VerifyCandidateFindingsAsync(candidateFindings, baseContext, pr, ct);

        var gateDecisions = await this._deterministicReviewFindingGate!.EvaluateAsync(candidateFindings, this._reviewInvariantFacts, ct);
        var reconciliation = this._summaryReconciliationService!.Reconcile(synthesis.Summary, candidateFindings, gateDecisions);

        if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
        {
            await this.RecordFinalGateProtocolAsync(
                baseContext.ProtocolRecorder,
                baseContext.ActiveProtocolId.Value,
                candidateFindings,
                gateDecisions,
                reconciliation,
                ct);
        }

        var publishedComments = MaterializePublishedComments(candidateFindings, gateDecisions);
        var result = new ReviewResult(reconciliation.FinalSummary, publishedComments);

        await this.RecordNativeCompletionAsync(job, baseContext, candidateFindings, gateDecisions, reconciliation, result, ct);
        return result;
    }

    private async Task<PrWideReviewPlan> RecordPlanAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        PrWideReviewPlan plan,
        CancellationToken ct)
    {
        await this.RecordStageEventAsync(
            baseContext,
            ReviewProtocolEventNames.PrWidePlanCreated,
            new
            {
                strategy = "pr_wide_agentic",
                stage = "planning",
                jobId = job.Id,
                planId = plan.PlanId,
            },
            new
            {
                planId = plan.PlanId,
                concerns = plan.Concerns,
                changedAreas = plan.ChangedAreas,
                investigationTasks = plan.InvestigationTasks,
                noInvestigationReason = plan.NoInvestigationReason,
            },
            ct);

        return plan;
    }

    private async Task<List<CandidateReviewFinding>> VerifyCandidateFindingsAsync(
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings,
        ReviewSystemContext baseContext,
        PullRequest pr,
        CancellationToken ct)
    {
        if (synthesizedFindings.Count == 0 || this._reviewClaimExtractor is null)
        {
            return synthesizedFindings.ToList();
        }

        var screenedFindings = this.ApplyDeterministicScreening(synthesizedFindings, pr, baseContext);
        var prLevelVerifier = new AiMicroReviewFindingVerifier();
        var verified = new List<CandidateReviewFinding>(screenedFindings.Count);

        foreach (var finding in screenedFindings)
        {
            if (finding.VerificationOutcome is not null)
            {
                verified.Add(finding);
                continue;
            }

            IReadOnlyList<ClaimDescriptor> claims;
            try
            {
                claims = this._reviewClaimExtractor.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
                {
                    await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                        baseContext.ActiveProtocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                stage = DetermineVerificationStage(finding),
                                degradedComponent = "claim_extraction",
                            }),
                        null,
                        ex.Message,
                        ct);
                }

                verified.Add(
                    WithVerificationOutcome(
                        finding,
                        VerificationOutcome.DegradedUnresolved(
                            finding.FindingId,
                            VerificationOutcome.DeterministicRulesEvaluator,
                            ReviewFindingGateReasonCodes.VerificationDegraded,
                            $"PR-wide claim extraction degraded: {ex.Message}")));
                continue;
            }

            if (claims.Count == 0)
            {
                verified.Add(
                    WithVerificationOutcome(
                        finding,
                        CreateNoClaimsVerificationOutcome(finding)));
                continue;
            }

            if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
            {
                await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                    baseContext.ActiveProtocolId.Value,
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

            var claim = claims[0];
            if (string.Equals(claim.Stage, ClaimDescriptor.LocalStage, StringComparison.Ordinal))
            {
                var workItems = claims
                    .Select(currentClaim => new VerificationWorkItem(
                        currentClaim,
                        finding.Provenance,
                        currentClaim.Stage,
                        VerificationWorkItem.AnchorOnlyScope,
                        false,
                        finding.Evidence))
                    .ToList();

                IReadOnlyList<VerificationOutcome> outcomes;
                try
                {
                    outcomes = await this._reviewFindingVerifier!.VerifyAsync(workItems, this._reviewInvariantFacts, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
                    {
                        await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                            baseContext.ActiveProtocolId.Value,
                            ReviewProtocolEventNames.VerificationDegraded,
                            JsonSerializer.Serialize(
                                new
                                {
                                    findingId = finding.FindingId,
                                    stage = ClaimDescriptor.LocalStage,
                                    degradedComponent = "local_verification",
                                }),
                            null,
                            ex.Message,
                            ct);
                    }

                    verified.Add(
                        WithVerificationOutcome(
                            finding,
                            VerificationOutcome.DegradedUnresolved(
                                finding.FindingId,
                                VerificationOutcome.DeterministicRulesEvaluator,
                                ReviewFindingGateReasonCodes.VerificationDegraded,
                                $"PR-wide local verification degraded: {ex.Message}",
                                claim.ClaimId)));
                    continue;
                }

                if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
                {
                    foreach (var localOutcome in outcomes)
                    {
                        await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                            baseContext.ActiveProtocolId.Value,
                            ReviewProtocolEventNames.VerificationLocalDecision,
                            JsonSerializer.Serialize(
                                new
                                {
                                    findingId = localOutcome.FindingId,
                                    claimId = localOutcome.ClaimId,
                                }),
                            JsonSerializer.Serialize(localOutcome, FinalGateJsonOptions),
                            null,
                            ct);
                    }
                }

                verified.Add(WithVerificationOutcome(finding, SelectPrimaryOutcome(outcomes, finding.FindingId)));
                continue;
            }

            if (this._reviewEvidenceCollector is null)
            {
                verified.Add(finding);
                continue;
            }

            var workItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                finding.Evidence);

            EvidenceBundle evidence;
            try
            {
                evidence = await this._reviewEvidenceCollector.CollectEvidenceAsync(workItem, baseContext.ReviewTools, pr.SourceBranch, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
                {
                    await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                        baseContext.ActiveProtocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                claimId = claim.ClaimId,
                                stage = ClaimDescriptor.PrLevelStage,
                                degradedComponent = "evidence_collection",
                            }),
                        null,
                        ex.Message,
                        ct);
                }

                verified.Add(
                    WithVerificationOutcome(
                        finding,
                        VerificationOutcome.DegradedUnresolved(
                            claim,
                            VerificationOutcome.AiMicroVerifierEvaluator,
                            ReviewFindingGateReasonCodes.VerificationDegraded,
                            $"PR-wide evidence collection degraded: {ex.Message}")));
                continue;
            }

            if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
            {
                await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                    baseContext.ActiveProtocolId.Value,
                    ReviewProtocolEventNames.VerificationEvidenceCollected,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = finding.FindingId,
                            claimId = claim.ClaimId,
                            coverageState = evidence.CoverageState,
                        }),
                    JsonSerializer.Serialize(evidence, FinalGateJsonOptions),
                    null,
                    ct);
            }

            var supportingFiles = evidence.EvidenceItems
                .Select(item => item.SourceId)
                .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var evidenceState = evidence.CoverageState switch
            {
                EvidenceBundle.CompleteCoverage => EvidenceReference.ResolvedState,
                EvidenceBundle.PartialCoverage => EvidenceReference.PartialState,
                _ => EvidenceReference.MissingState,
            };

            var updatedEvidence = finding.Evidence is null
                ? new EvidenceReference([], supportingFiles, evidenceState, "review_context_tools")
                : new EvidenceReference(
                    finding.Evidence.SupportingFindingIds,
                    supportingFiles.Length > 0 ? supportingFiles : finding.Evidence.SupportingFiles,
                    evidenceState,
                    finding.Evidence.EvidenceSource);

            var evidenceBackedWorkItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                updatedEvidence);
            var outcome = (await prLevelVerifier.VerifyAsync([evidenceBackedWorkItem], [], ct))[0];

            if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
            {
                await baseContext.ProtocolRecorder.RecordVerificationEventAsync(
                    baseContext.ActiveProtocolId.Value,
                    ReviewProtocolEventNames.VerificationPrDecision,
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

            verified.Add(
                new CandidateReviewFinding(
                    finding.FindingId,
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    updatedEvidence,
                    finding.CandidateSummaryText,
                    finding.InvariantCheckContext,
                    outcome));
        }

        return verified;
    }

    private List<CandidateReviewFinding> ApplyDeterministicScreening(
        IReadOnlyList<CandidateReviewFinding> findings,
        PullRequest pr,
        ReviewSystemContext baseContext)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        var normalizedFindings = findings
            .Select(finding => NormalizePublicationAnchor(finding, pr))
            .ToList();
        var fallbackFile = pr.ChangedFiles.FirstOrDefault()
                           ?? new ChangedFile("__pr_wide__", ChangeType.Edit, string.Empty, string.Empty);
        var decisions = new List<CommentRelevanceFilterDecision>(normalizedFindings.Count);

        foreach (var finding in normalizedFindings)
        {
            var comment = new ReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message);
            var hardGuardDecision = EvaluateDeterministicHardGuards(comment);
            if (hardGuardDecision is not null)
            {
                decisions.Add(hardGuardDecision);
                continue;
            }

            IReadOnlyList<ClaimDescriptor> screeningClaims = [];
            if (this._reviewClaimExtractor is not null)
            {
                try
                {
                    screeningClaims = this._reviewClaimExtractor.ExtractClaims(finding);
                }
                catch
                {
                    screeningClaims = [];
                }
            }

            var requestFile = ResolveRequestFile(finding, pr, fallbackFile);
            var request = new CommentRelevanceFilterRequest(
                Guid.Empty,
                null,
                "pr-wide-deterministic",
                requestFile.Path,
                requestFile,
                pr,
                [comment],
                baseContext,
                baseContext.ActiveProtocolId);
            decisions.Add(EvaluatePrWideCommentRelevance(request, finding, comment, screeningClaims));
        }

        HeuristicCommentRelevanceFilter.ApplyDuplicateLocalPattern(decisions);

        var screenedFindings = new List<CandidateReviewFinding>(normalizedFindings.Count);
        for (var index = 0; index < normalizedFindings.Count; index++)
        {
            var finding = normalizedFindings[index];
            var decision = decisions[index];
            if (decision.IsKeep)
            {
                screenedFindings.Add(finding);
                continue;
            }

            var recommendedDisposition = decision.ReasonCodes.Count == 1 &&
                                         string.Equals(decision.ReasonCodes[0], CommentRelevanceReasonCodes.SummaryLevelOnly, StringComparison.Ordinal)
                ? FinalGateDecision.SummaryOnlyDisposition
                : FinalGateDecision.DropDisposition;

            screenedFindings.Add(
                WithVerificationOutcome(
                    finding,
                    new VerificationOutcome(
                        $"{finding.FindingId}:claim:screening",
                        finding.FindingId,
                        VerificationOutcome.NotApplicableKind,
                        recommendedDisposition,
                        decision.ReasonCodes,
                        [],
                        VerificationOutcome.NoEvidence,
                        recommendedDisposition == FinalGateDecision.SummaryOnlyDisposition
                            ? "Candidate was retained only as summary context after deterministic PR-wide screening."
                            : "Candidate was dropped by deterministic PR-wide screening before deeper verification.",
                        VerificationOutcome.DeterministicRulesEvaluator,
                        false)));
        }

        return screenedFindings;
    }

    private static CommentRelevanceFilterDecision? EvaluateDeterministicHardGuards(ReviewComment comment)
    {
        var result = new ReviewResult(string.Empty, [comment]);
        if (ReviewCommentProcessing.FilterSpeculativeComments(result).Comments.Count == 0)
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.DiscardDecision,
                comment,
                [CommentRelevanceReasonCodes.HedgingLanguage],
                CommentRelevanceFilterDecision.DeterministicScreeningSource);
        }

        if (ReviewCommentProcessing.StripInfoComments(result).Comments.Count == 0)
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.DiscardDecision,
                comment,
                [CommentRelevanceReasonCodes.InfoSeverityNonActionable],
                CommentRelevanceFilterDecision.DeterministicScreeningSource);
        }

        if (ReviewCommentProcessing.FilterVagueSuggestions(result).Comments.Count == 0)
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.DiscardDecision,
                comment,
                [CommentRelevanceReasonCodes.NonActionableSuggestion],
                CommentRelevanceFilterDecision.DeterministicScreeningSource);
        }

        return null;
    }

    private static CommentRelevanceFilterDecision EvaluatePrWideCommentRelevance(
        CommentRelevanceFilterRequest request,
        CandidateReviewFinding finding,
        ReviewComment comment,
        IReadOnlyList<ClaimDescriptor> claims)
    {
        var decision = HeuristicCommentRelevanceFilter.EvaluateComment(request, comment, false);
        if (decision.IsKeep)
        {
            return decision;
        }

        var adaptedReasonCodes = decision.ReasonCodes
            .Where(code => !ShouldIgnoreRelevanceReason(finding, claims, code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (adaptedReasonCodes.Length == 0)
        {
            return new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                [],
                CommentRelevanceFilterDecision.DeterministicScreeningSource);
        }

        return new CommentRelevanceFilterDecision(
            decision.Decision,
            comment,
            adaptedReasonCodes,
            decision.DecisionSource);
    }

    private static bool ShouldIgnoreRelevanceReason(CandidateReviewFinding finding, IReadOnlyList<ClaimDescriptor> claims, string reasonCode)
    {
        var isCrossFileCandidate = string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
                                   || finding.Evidence?.SupportingFiles.Count > 1;
        if (isCrossFileCandidate)
        {
            return reasonCode is CommentRelevanceReasonCodes.UnverifiableCrossFileClaim
                or CommentRelevanceReasonCodes.MissingConcreteObservable
                or CommentRelevanceReasonCodes.SeverityOverstated;
        }

        var hasDeterministicLocalClaim = claims.Any(claim =>
            string.Equals(claim.Stage, ClaimDescriptor.LocalStage, StringComparison.Ordinal)
            && string.Equals(claim.VerificationMode, ClaimDescriptor.DeterministicOnlyMode, StringComparison.Ordinal));
        return hasDeterministicLocalClaim
               && string.Equals(reasonCode, CommentRelevanceReasonCodes.HedgingLanguage, StringComparison.Ordinal);
    }

    private static CandidateReviewFinding NormalizePublicationAnchor(CandidateReviewFinding finding, PullRequest pr)
    {
        if (string.IsNullOrWhiteSpace(finding.FilePath) || finding.LineNumber is null || finding.LineNumber.Value < 1)
        {
            return finding;
        }

        var changedFile = pr.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, finding.FilePath, StringComparison.OrdinalIgnoreCase));
        if (changedFile is null)
        {
            return WithPublicationAnchor(finding, null, null);
        }

        var totalLines = CountLines(changedFile.FullContent);
        return totalLines > 0 && finding.LineNumber.Value > totalLines
            ? WithPublicationAnchor(finding, null, null)
            : finding;
    }

    private static ChangedFile ResolveRequestFile(CandidateReviewFinding finding, PullRequest pr, ChangedFile fallbackFile)
    {
        if (!string.IsNullOrWhiteSpace(finding.FilePath))
        {
            var matched = pr.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, finding.FilePath, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        return fallbackFile;
    }

    private static int CountLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.Split('\n').Length;
    }

    private static CandidateReviewFinding WithPublicationAnchor(CandidateReviewFinding finding, string? filePath, int? lineNumber)
    {
        return new CandidateReviewFinding(
            finding.FindingId,
            finding.Provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            filePath,
            lineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            finding.VerificationOutcome);
    }

    private static CandidateReviewFinding WithVerificationOutcome(CandidateReviewFinding finding, VerificationOutcome outcome)
    {
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
            finding.InvariantCheckContext,
            outcome);
    }

    private static VerificationOutcome CreateNoClaimsVerificationOutcome(CandidateReviewFinding finding)
    {
        var isCrossFileCandidate = string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
                                   || finding.Evidence?.SupportingFiles.Count > 1;
        var reasonCode = isCrossFileCandidate
            ? ReviewFindingGateReasonCodes.MissingMultiFileEvidence
            : ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport;
        var evidenceSummary = isCrossFileCandidate
            ? "PR-wide claim extraction produced no verifiable claim descriptors, so the finding was retained only as summary context."
            : "Claim extraction produced no verifiable claim descriptors, so the finding could not be published without bounded verification support.";

        return new VerificationOutcome(
            $"{finding.FindingId}:claim:none",
            finding.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [reasonCode],
            [],
            VerificationOutcome.NoEvidence,
            evidenceSummary,
            VerificationOutcome.DeterministicRulesEvaluator,
            false);
    }

    private static VerificationOutcome SelectPrimaryOutcome(IReadOnlyList<VerificationOutcome> outcomes, string findingId)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        return outcomes.FirstOrDefault(outcome => outcome.BlocksPublication)
               ?? outcomes.FirstOrDefault(outcome => string.Equals(
                   outcome.RecommendedDisposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
               ?? outcomes.FirstOrDefault()
               ?? VerificationOutcome.DegradedUnresolved(
                   findingId,
                   VerificationOutcome.DeterministicRulesEvaluator,
                   ReviewFindingGateReasonCodes.VerificationDegraded,
                   "PR-wide local verification returned no outcomes.");
    }

    private static string DetermineVerificationStage(CandidateReviewFinding finding)
    {
        return string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal)
               || finding.Evidence?.SupportingFiles.Count > 1
            ? ClaimDescriptor.PrLevelStage
            : ClaimDescriptor.LocalStage;
    }

    private async Task RecordFinalGateProtocolAsync(
        IProtocolRecorder protocolRecorder,
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
            await protocolRecorder.RecordReviewFindingGateEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewFindingGateDecision,
                summaryJson,
                JsonSerializer.Serialize(recordedDecision, FinalGateJsonOptions),
                null,
                ct);
        }
    }

    private static IReadOnlyList<ReviewComment> MaterializePublishedComments(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        var decisionsById = decisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        return candidateFindings
            .Where(finding => decisionsById.TryGetValue(finding.FindingId, out var decision)
                              && string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(finding => new ReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
    }

    private async Task RecordStageEventAsync(
        ReviewSystemContext baseContext,
        string eventName,
        object details,
        object output,
        CancellationToken ct)
    {
        if (!baseContext.ActiveProtocolId.HasValue || baseContext.ProtocolRecorder is null)
        {
            return;
        }

        await baseContext.ProtocolRecorder.RecordPrWideStageEventAsync(
            baseContext.ActiveProtocolId.Value,
            eventName,
            JsonSerializer.Serialize(details),
            JsonSerializer.Serialize(output),
            null,
            ct);
    }

    private static ReviewSystemContext CloneContext(
        ReviewSystemContext source,
        Guid? activeProtocolId,
        IProtocolRecorder? protocolRecorder)
    {
        return new ReviewSystemContext(source.ClientSystemMessage, source.RepositoryInstructions, source.ReviewTools)
        {
            LoopMetrics = source.LoopMetrics,
            ActiveProtocolId = activeProtocolId,
            PerFileHint = source.PerFileHint,
            ProtocolRecorder = protocolRecorder,
            ExclusionRules = source.ExclusionRules,
            DismissedPatterns = source.DismissedPatterns,
            PromptOverrides = source.PromptOverrides,
            TierChatClient = source.TierChatClient,
            ModelId = source.ModelId,
            DefaultReviewChatClient = source.DefaultReviewChatClient,
            DefaultReviewModelId = source.DefaultReviewModelId,
            RuntimeCapabilities = source.RuntimeCapabilities,
            ReviewSession = source.ReviewSession,
            Temperature = source.Temperature,
            EnableProRV = source.EnableProRV,
            AugmentationMode = source.AugmentationMode,
            PassKind = source.PassKind,
            PromptExperiment = source.PromptExperiment,
            SkippedSteps = source.SkippedSteps,
        };
    }

    private static string BuildConcern(string filePath)
    {
        if (filePath.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("Registration", StringComparison.OrdinalIgnoreCase))
        {
            return "Check composition-root and dependency registration changes.";
        }

        if (filePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return "Check whether tests cover the behavioral contract changes.";
        }

        return $"Check behavioral impact around {filePath}.";
    }

    private static int ScoreFilePriority(string filePath, PullRequest pr)
    {
        var file = pr.ChangedFiles.FirstOrDefault(changed => string.Equals(changed.Path, filePath, StringComparison.Ordinal));
        var diff = file?.UnifiedDiff ?? string.Empty;

        var score = 0;
        if (filePath.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("Registration", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("Startup", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (filePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (diff.Contains("critical", StringComparison.OrdinalIgnoreCase) || diff.Contains("Add", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }

    private static bool IsInlineComment(ReviewComment comment)
    {
        return !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber is > 0;
    }

    private static string? CreateSummaryPreview(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        return summary.Length <= 200
            ? summary
            : summary[..200];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "PR-wide Stage A/B/C artifacts recorded for job {JobId}")]
    private static partial void LogArtifactsRecorded(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to open PR-wide protocol pass for job {JobId}")]
    private static partial void LogProtocolBeginFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PR-wide {Stage} stage fell back to deterministic artifacts for job {JobId}: {Reason}")]
    private static partial void LogStageFallback(ILogger logger, string stage, Guid jobId, string reason);
}
