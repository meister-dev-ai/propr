// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>
///     Reviewing-owned protocol event names used by the comment relevance filter workflow.
/// </summary>
public static class ReviewProtocolEventNames
{
    public const string CommentRelevanceFilterOutput = "comment_relevance_filter_output";
    public const string CommentRelevanceFilterDegraded = "comment_relevance_filter_degraded";
    public const string CommentRelevanceEvaluatorDegraded = "comment_relevance_evaluator_degraded";
    public const string CommentRelevanceFilterSelectionFallback = "comment_relevance_filter_selection_fallback";
    public const string CommentRelevanceEvaluatorAiCall = "ai_call_comment_relevance_evaluator";
    public const string ReviewFindingGateSummary = "review_finding_gate_summary";
    public const string ReviewFindingGateDecision = "review_finding_gate_decision";
    public const string VerificationClaimsExtracted = "verification_claims_extracted";
    public const string VerificationLocalDecision = "verification_local_decision";
    public const string VerificationEvidenceCollected = "verification_evidence_collected";
    public const string VerificationPrDecision = "verification_pr_decision";
    public const string SummaryReconciliation = "summary_reconciliation";
    public const string VerificationDegraded = "verification_degraded";
}
