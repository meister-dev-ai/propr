// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>
///     Reviewing-owned protocol event names used by strategy selection, PR-wide orchestration,
///     comment relevance, verification, and final-gate workflows.
/// </summary>
public static class ReviewProtocolEventNames
{
    public const string LateSteeringBaselinePassCompleted = "late_steering_baseline_pass_completed";
    public const string LateSteeringAugmentationPassCompleted = "late_steering_augmentation_pass_completed";
    public const string LateSteeringMergeCompleted = "late_steering_merge_completed";
    public const string ReviewPipelineProfileApplied = "review_pipeline_profile_applied";
    public const string ReviewStrategySelected = "review_strategy_selected";
    public const string AgenticFilePlanCreated = "agentic_file_plan_created";
    public const string AgenticFileInvestigationLaunched = "agentic_file_investigation_launched";
    public const string AgenticFileInvestigationResult = "agentic_file_investigation_result";
    public const string AgenticFileEvidenceCollected = "agentic_file_evidence_collected";
    public const string AgenticFileDegraded = "agentic_file_degraded";
    public const string AgenticFileFollowUpDependencyRecorded = "agentic_file_follow_up_dependency_recorded";
    public const string AgenticFileFollowUpDiagnosticsOnly = "agentic_file_follow_up_diagnostics_only";
    public const string PrWidePlanCreated = "pr_wide_plan_created";
    public const string PrWideInvestigationLaunched = "pr_wide_investigation_launched";
    public const string PrWideInvestigationResult = "pr_wide_investigation_result";
    public const string PrWideEvidenceCollected = "pr_wide_evidence_collected";
    public const string PrWideSynthesisCompleted = "pr_wide_synthesis_completed";
    public const string PrWideCandidateMerged = "pr_wide_candidate_merged";
    public const string PrWideVerificationCompleted = "pr_wide_verification_completed";
    public const string PrWideFinalGateDecision = "pr_wide_final_gate_decision";
    public const string PrWideSummaryReconciled = "pr_wide_summary_reconciled";
    public const string PrWidePublicationPrepared = "pr_wide_publication_prepared";
    public const string CommentRelevanceFilterOutput = "comment_relevance_filter_output";
    public const string CommentRelevanceFilterDegraded = "comment_relevance_filter_degraded";
    public const string CommentRelevanceEvaluatorDegraded = "comment_relevance_evaluator_degraded";
    public const string CommentRelevanceFilterSelectionFallback = "comment_relevance_filter_selection_fallback";
    public const string CommentRelevanceEvaluatorAiCall = "ai_call_comment_relevance_evaluator";
    public const string ProRVPrefilterAiCall = "ai_call_prorv_prefilter";
    public const string ProRVPrefilterStarted = "prorv_prefilter_started";
    public const string ProRVPrefilterSkipped = "prorv_prefilter_skipped";
    public const string ProRVPrefilterCompleted = "prorv_prefilter_completed";
    public const string ProRVPrefilterFailed = "prorv_prefilter_failed";
    public const string ProRVFocusedGuidanceApplied = "prorv_focused_guidance_applied";
    public const string ReviewFindingGateSummary = "review_finding_gate_summary";
    public const string ReviewFindingGateDecision = "review_finding_gate_decision";
    public const string ReviewAgentSessionBinding = "review_agent_session_binding";
    public const string ReviewAgentSessionTurn = "review_agent_session_turn";
    public const string ReviewAgentSessionFallback = "review_agent_session_fallback";
    public const string VerificationClaimsExtracted = "verification_claims_extracted";
    public const string VerificationLocalDecision = "verification_local_decision";
    public const string VerificationEvidenceCollected = "verification_evidence_collected";
    public const string VerificationPrDecision = "verification_pr_decision";
    public const string SummaryReconciliation = "summary_reconciliation";
    public const string VerificationDegraded = "verification_degraded";
    public const string RepeatedJudgmentDecision = "repeated_judgment_decision";
    public const string PromptStageEvidenceRecorded = "prompt_stage_evidence_recorded";
    public const string ReviewStepSkipped = "review_step_skipped";
}
