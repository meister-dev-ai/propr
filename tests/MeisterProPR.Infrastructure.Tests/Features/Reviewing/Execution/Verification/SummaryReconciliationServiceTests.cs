// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class SummaryReconciliationServiceTests
{
    [Fact]
    public void Reconcile_WithSummaryOnlyDecision_LeavesNarrativeForGroundingToFooter()
    {
        // Reconciliation owns only the narrative. When the model prose does not reference a dropped
        // finding it passes the narrative through untouched; the summary-only footer is appended once,
        // downstream, by the grounding step — so this service must not append it here.
        var sut = new SummaryReconciliationService();
        var findings = new[]
        {
            new CandidateReviewFinding(
                "finding-summary-1",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Architecture concerns span the PR and should be revisited.",
                "architecture",
                candidateSummaryText: "Potential architecture concern noted."),
        };

        var result = sut.Reconcile(
            "Base summary.",
            findings,
            [
                new FinalGateDecision(
                    "finding-summary-1",
                    FinalGateDecision.SummaryOnlyDisposition,
                    [ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
                    "verification_outcome_rules",
                    [],
                    null,
                    "Potential architecture concern noted."),
            ]);

        Assert.Equal("Base summary.", result.OriginalSummary);
        Assert.Equal("Base summary.", result.FinalSummary);
        Assert.DoesNotContain("Summary-only findings:", result.FinalSummary);
        Assert.Contains("finding-summary-1", result.SummaryOnlyFindingIds);
        Assert.False(result.RewritePerformed);
        Assert.Equal("deterministic_summary_passthrough", result.RuleSource);
    }

    [Fact]
    public void Reconcile_WithDroppedFindings_RewritesUnsafeOriginalSummary()
    {
        var sut = new SummaryReconciliationService();
        var findings = new[]
        {
            new CandidateReviewFinding(
                "finding-publish-1",
                new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
                CommentSeverity.Warning,
                "Confirmed null dereference in ExecuteAsync.",
                CandidateReviewFinding.PerFileCommentCategory,
                "src/Foo.cs",
                12),
            new CandidateReviewFinding(
                "finding-drop-1",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Missing DI registration across the pipeline.",
                CandidateReviewFinding.CrossCuttingCategory),
        };

        var result = sut.Reconcile(
            "The PR definitely has a missing DI registration across the pipeline.",
            findings,
            [
                new FinalGateDecision(
                    "finding-publish-1",
                    FinalGateDecision.PublishDisposition,
                    [ReviewFindingGateReasonCodes.DefaultPublish],
                    "default_publish_rules",
                    [],
                    null,
                    null),
                new FinalGateDecision(
                    "finding-drop-1",
                    FinalGateDecision.DropDisposition,
                    [ReviewFindingGateReasonCodes.InvariantContradiction],
                    "invariant_contradiction_rules",
                    ["review_comment_message_required"],
                    null,
                    null),
            ]);

        // The unsafe narrative is replaced with a neutral drop notice. Publishable counts are the
        // grounding step's concern, not reconciliation's, so they are intentionally absent here.
        Assert.DoesNotContain("missing DI registration", result.FinalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishable finding", result.FinalSummary);
        Assert.Contains("1 candidate finding was dropped", result.FinalSummary);
        Assert.Contains("finding-drop-1", result.DroppedFindingIds);
        Assert.True(result.RewritePerformed);
        Assert.Equal("deterministic_summary_rewrite", result.RuleSource);
    }

    [Fact]
    public void Reconcile_WhenAllFindingsDropped_ReplacesNarrativeWithDropNotice()
    {
        var sut = new SummaryReconciliationService();
        var findings = new[]
        {
            new CandidateReviewFinding(
                "finding-drop-1",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Issues everywhere.",
                CandidateReviewFinding.CrossCuttingCategory),
        };

        var result = sut.Reconcile(
            "Original synthesis said there were issues everywhere.",
            findings,
            [
                new FinalGateDecision(
                    "finding-drop-1",
                    FinalGateDecision.DropDisposition,
                    [ReviewFindingGateReasonCodes.InvariantContradiction],
                    "invariant_contradiction_rules",
                    ["review_comment_message_required"],
                    null,
                    null),
            ]);

        // Reconciliation replaces the unsafe narrative with the drop notice; the terminal
        // "No publishable or summary-only findings remained" line is the grounding step's output.
        Assert.Equal("1 candidate finding was dropped during verification.", result.FinalSummary);
        Assert.DoesNotContain("issues everywhere", result.FinalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.RewritePerformed);
        Assert.Contains("finding-drop-1", result.DroppedFindingIds);
    }
}
