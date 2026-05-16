// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Stage A plan that identifies file-scoped concerns and bounded follow-up investigations.</summary>
/// <param name="PlanId">Stable plan identifier for the anchor file.</param>
/// <param name="AnchorFilePath">Primary file path being reviewed.</param>
/// <param name="Concerns">File-scoped concerns worth investigating.</param>
/// <param name="ChangedAreas">Logical areas touched by the file change.</param>
/// <param name="InvestigationTasks">Bounded investigation tasks derived from the plan.</param>
/// <param name="NoInvestigationReason">Explanation when the planner decides no deeper follow-up is required.</param>
public sealed record AgenticFileReviewPlan(
    string PlanId,
    string AnchorFilePath,
    IReadOnlyList<string> Concerns,
    IReadOnlyList<string> ChangedAreas,
    IReadOnlyList<AgenticFileInvestigationTask> InvestigationTasks,
    string? NoInvestigationReason = null);

/// <summary>Bounded Stage B investigation request for one file-scoped concern.</summary>
/// <param name="TaskId">Stable task identifier within the file review.</param>
/// <param name="TaskType">Investigation task type such as concern or sibling_file.</param>
/// <param name="TriggerFamily">Explicit trigger family that justified the deeper follow-up.</param>
/// <param name="Concern">Concern this task investigates.</param>
/// <param name="SeedFilePaths">Planner-approved file paths that scope the investigation.</param>
/// <param name="AllowedTools">Tool names that the investigation may invoke.</param>
/// <param name="MaxToolCalls">Maximum tool calls allowed for the investigation.</param>
public sealed record AgenticFileInvestigationTask(
    string TaskId,
    string TaskType,
    string TriggerFamily,
    string Concern,
    IReadOnlyList<string> SeedFilePaths,
    IReadOnlyList<string> AllowedTools,
    int MaxToolCalls);

/// <summary>One bounded Stage B tool attempt made during a file-scoped investigation.</summary>
/// <param name="ToolName">Tool name that was attempted.</param>
/// <param name="Status">Outcome such as success, blocked_not_allowed, or blocked_budget_exhausted.</param>
/// <param name="Target">Primary target the tool was asked to inspect.</param>
public sealed record AgenticFileToolUsage(
    string ToolName,
    string Status,
    string? Target = null);

/// <summary>Result of one bounded file-scoped investigation.</summary>
/// <param name="TaskId">Investigation task identifier.</param>
/// <param name="Status">Investigation status such as completed, skipped, or degraded.</param>
/// <param name="Evidence">Evidence gathered through review tools.</param>
/// <param name="CandidateFindings">Candidate findings produced by the investigation.</param>
/// <param name="ToolUsage">Bounded tool attempts recorded for the investigation.</param>
/// <param name="Degraded">Whether the investigation completed with reduced capability.</param>
/// <param name="DiagnosticsOnly">Whether the investigation result is strictly diagnostic and may not publish candidate findings.</param>
/// <param name="EvidenceSetId">Stable evidence-set identifier for downstream provenance and repeated judgment.</param>
/// <param name="DependencyRecorded">Whether a surviving finding depended on this follow-up.</param>
public sealed record AgenticFileInvestigationResult(
    string TaskId,
    string Status,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<AgenticFileCandidateFinding> CandidateFindings,
    IReadOnlyList<AgenticFileToolUsage> ToolUsage,
    bool Degraded,
    bool DiagnosticsOnly = false,
    string? EvidenceSetId = null,
    bool DependencyRecorded = false);

/// <summary>Candidate finding generated before verification and final-gate disposition.</summary>
/// <param name="Id">Stable candidate identifier within the file review.</param>
/// <param name="Message">Review finding message.</param>
/// <param name="Category">Finding category used by downstream gates and diagnostics.</param>
/// <param name="Confidence">Confidence score assigned before verification.</param>
/// <param name="EvidenceReference">Evidence references that support the candidate.</param>
/// <param name="RelatedFilePaths">Files related to the candidate.</param>
/// <param name="Severity">Severity level of the candidate finding.</param>
/// <param name="FilePath">File path associated with the candidate finding.</param>
/// <param name="LineNumber">Line number associated with the candidate finding.</param>
/// <param name="CandidateSummaryText">Summary text for the candidate finding.</param>
/// <param name="InvariantCheckContext">Context for invariant checks related to the candidate finding.</param>
/// <param name="SupportSource">Optional machine-readable support source attached before final publication.</param>
public sealed record AgenticFileCandidateFinding(
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
    IReadOnlyDictionary<string, string>? InvariantCheckContext = null,
    string? SupportSource = null)
{
    /// <summary>
    ///     Converts the agentic file candidate finding to a candidate review finding by incorporating provenance, optional finding ID, and optional verification
    ///     outcome.
    /// </summary>
    /// <param name="provenance">Provenance information for the candidate finding.</param>
    /// <param name="findingId">Optional finding identifier.</param>
    /// <param name="verificationOutcome">Optional verification outcome.</param>
    /// <returns>A candidate review finding.</returns>
    public CandidateReviewFinding ToCandidateReviewFinding(
        CandidateFindingProvenance provenance,
        string? findingId = null,
        VerificationOutcome? verificationOutcome = null)
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
            verificationOutcome);
    }
}

/// <summary>Verification and final-gate disposition for one file-scoped candidate.</summary>
/// <param name="CandidateId">Candidate identifier.</param>
/// <param name="VerificationOutcome">Evidence-backed verification outcome.</param>
/// <param name="FinalGateDecision">Deterministic final-gate decision.</param>
public sealed record AgenticFileVerificationArtifact(
    string CandidateId,
    VerificationOutcome VerificationOutcome,
    FinalGateDecision FinalGateDecision);
