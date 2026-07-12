// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Stage A plan that identifies PR-wide concerns and bounded follow-up investigations.</summary>
/// <param name="PlanId">Stable Stage A plan identifier.</param>
/// <param name="Concerns">High-level concerns worth investigating across the PR.</param>
/// <param name="ChangedAreas">Logical areas affected by the reviewed changes.</param>
/// <param name="InvestigationTasks">Bounded investigation tasks derived from the plan.</param>
/// <param name="NoInvestigationReason">Explanation when Stage A decides no investigations are required.</param>
public sealed record PrWideReviewPlan(
    string PlanId,
    IReadOnlyList<string> Concerns,
    IReadOnlyList<string> ChangedAreas,
    IReadOnlyList<PrWideInvestigationTask> InvestigationTasks,
    string? NoInvestigationReason = null);

/// <summary>Bounded Stage B investigation request for one concern.</summary>
/// <param name="Id">Stable task identifier within the strategy run.</param>
/// <param name="TaskType">Investigation task type such as concern or file_group.</param>
/// <param name="Concern">Concern this task investigates.</param>
/// <param name="SeedFilePaths">Initial changed files that scoped the investigation.</param>
/// <param name="AllowedTools">Tool names that this investigation may invoke.</param>
/// <param name="MaxToolCalls">Maximum tool calls allowed for this investigation.</param>
public sealed record PrWideInvestigationTask(
    string Id,
    string TaskType,
    string Concern,
    IReadOnlyList<string> SeedFilePaths,
    IReadOnlyList<string> AllowedTools,
    int MaxToolCalls);

/// <summary>One bounded Stage B tool attempt made by an investigation.</summary>
/// <param name="ToolName">Tool name that was attempted.</param>
/// <param name="Status">Outcome such as success, blocked_not_allowed, or blocked_budget_exhausted.</param>
/// <param name="Target">Primary target the tool was asked to inspect.</param>
public sealed record PrWideToolUsage(
    string ToolName,
    string Status,
    string? Target = null);

/// <summary>Result of a bounded Stage B investigation.</summary>
/// <param name="TaskId">Investigation task identifier.</param>
/// <param name="Status">Investigation status such as completed, skipped, or degraded.</param>
/// <param name="Evidence">Evidence gathered through review tools.</param>
/// <param name="CandidateFindings">Candidate findings produced by the investigation.</param>
/// <param name="ToolUsage">Bounded tool attempts recorded for the investigation.</param>
/// <param name="Degraded">Whether the investigation completed with reduced capability.</param>
public sealed record PrWideInvestigationResult(
    string TaskId,
    string Status,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<PrWideCandidateFinding> CandidateFindings,
    IReadOnlyList<PrWideToolUsage> ToolUsage,
    bool Degraded);

/// <summary>Candidate finding generated before verification and final-gate disposition.</summary>
/// <param name="Id">Stable candidate identifier within the strategy run.</param>
/// <param name="Message">Review finding message.</param>
/// <param name="Category">Finding category used by downstream gates and diagnostics.</param>
/// <param name="Confidence">Confidence score assigned before verification.</param>
/// <param name="EvidenceReference">Evidence references that support the candidate.</param>
/// <param name="RelatedFilePaths">Files related to the candidate.</param>
public sealed record PrWideCandidateFinding(
    string Id,
    string Message,
    string Category,
    ConfidenceScore Confidence,
    EvidenceReference EvidenceReference,
    IReadOnlyList<string> RelatedFilePaths,
    CommentSeverity Severity = CommentSeverity.Warning,
    string? FilePath = null,
    int? LineNumber = null,
    string? CandidateSummaryText = null,
    IReadOnlyDictionary<string, string>? InvariantCheckContext = null)
{
    public CandidateReviewFinding ToCandidateReviewFinding(
        CandidateFindingProvenance provenance,
        string? findingId = null,
        VerificationOutcome? verificationOutcome = null,
        ChangedLineRelation? scopeRelation = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        return new CandidateReviewFinding(
            string.IsNullOrWhiteSpace(findingId) ? this.Id : findingId,
            provenance,
            this.Severity,
            this.Message,
            this.Category,
            this.FilePath,
            this.LineNumber,
            this.EvidenceReference,
            this.CandidateSummaryText,
            this.InvariantCheckContext,
            verificationOutcome,
            scopeRelation);
    }
}

/// <summary>Stage C synthesis output that becomes the input to verification and final gating.</summary>
/// <param name="Summary">Draft PR-wide summary produced before verification reconciliation.</param>
/// <param name="CandidateFindings">Normalized candidate findings emitted by Stage C.</param>
public sealed record PrWideSynthesisResult(
    string Summary,
    IReadOnlyList<PrWideCandidateFinding> CandidateFindings);

/// <summary>Verification and final-gate disposition for a PR-wide candidate.</summary>
/// <param name="CandidateId">Candidate identifier.</param>
/// <param name="VerificationOutcome">Evidence-backed verification outcome.</param>
/// <param name="FinalGateDecision">Deterministic final-gate decision.</param>
public sealed record PrWideVerificationArtifact(
    string CandidateId,
    VerificationOutcome VerificationOutcome,
    FinalGateDecision FinalGateDecision);
