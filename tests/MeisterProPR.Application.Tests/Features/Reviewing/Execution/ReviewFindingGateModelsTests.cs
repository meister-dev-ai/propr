// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewFindingGateModelsTests
{
    [Fact]
    public void CandidateFindingProvenance_RequiresOriginKind()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new CandidateFindingProvenance(
                string.Empty,
                "synthesis"));

        Assert.Contains("Origin kind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceReference_HasResolvedMultiFileEvidence_UsesDistinctSupportingFiles()
    {
        var evidence = new EvidenceReference(
            ["finding-001", "finding-002"],
            ["src/Foo.cs", "src/Bar.cs", "src/Foo.cs"],
            EvidenceReference.ResolvedState,
            "synthesis_payload");

        Assert.True(evidence.HasResolvedMultiFileEvidence);
    }

    [Fact]
    public void EvidenceReference_ResolvedSupportingFilesAreHintsUntilVerifiedOutcomePublishes()
    {
        var finding = new CandidateReviewFinding(
            "finding-cc-001",
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
            CommentSeverity.Warning,
            "Potential cross-cutting issue.",
            CandidateReviewFinding.CrossCuttingCategory,
            evidence: new EvidenceReference(
                ["finding-001", "finding-002"],
                ["src/Foo.cs", "src/Bar.cs"],
                EvidenceReference.ResolvedState,
                "synthesis_payload"));

        Assert.True(finding.Evidence?.HasResolvedMultiFileEvidence);
        Assert.Null(finding.VerificationOutcome);
    }

    [Fact]
    public void FinalGateDecision_SummaryOnlyWithoutSummaryText_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FinalGateDecision(
                "finding-001",
                FinalGateDecision.SummaryOnlyDisposition,
                [ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
                "cross_cutting_evidence_rules",
                [],
                null,
                null));

        Assert.Contains("SummaryOnly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalGateDecision_ToRecordedDecision_PreservesAuditFields()
    {
        var finding = new CandidateReviewFinding(
            "finding-cc-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.SynthesizedCrossCuttingOrigin,
                "synthesis"),
            CommentSeverity.Warning,
            "Potential cross-cutting issue.",
            CandidateReviewFinding.CrossCuttingCategory,
            evidence: new EvidenceReference(
                ["finding-001", "finding-002"],
                ["src/Foo.cs", "src/Bar.cs"],
                EvidenceReference.ResolvedState,
                "synthesis_payload"));

        var decision = new FinalGateDecision(
            finding.FindingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.EvidenceResolved],
            "cross_cutting_evidence_rules",
            [],
            finding.Evidence,
            null);

        var recorded = decision.ToRecordedDecision(finding);

        Assert.Equal(finding.FindingId, recorded.FindingId);
        Assert.Equal(FinalGateDecision.PublishDisposition, recorded.Disposition);
        Assert.Equal(CandidateReviewFinding.CrossCuttingCategory, recorded.Category);
        Assert.Equal(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, recorded.Provenance.OriginKind);
        Assert.Equal(EvidenceReference.ResolvedState, recorded.Evidence?.EvidenceResolutionState);
        Assert.Contains(ReviewFindingGateReasonCodes.EvidenceResolved, recorded.ReasonCodes);
    }

    [Fact]
    public void RecordedFinalGateSummary_FromFindingsAndDecisions_ComputesDispositionAndCategoryCounts()
    {
        var findings = new[]
        {
            new CandidateReviewFinding(
                "finding-001",
                new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
                CommentSeverity.Warning,
                "Per-file issue.",
                CandidateReviewFinding.PerFileCommentCategory,
                "src/Foo.cs",
                10),
            new CandidateReviewFinding(
                "finding-002",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Cross-cutting issue.",
                CandidateReviewFinding.CrossCuttingCategory),
            new CandidateReviewFinding(
                "finding-003",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Blocked issue.",
                CandidateReviewFinding.CrossCuttingCategory),
        };

        var decisions = new[]
        {
            new FinalGateDecision(
                "finding-001",
                FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.DefaultPublish],
                "default_publish_rules",
                [],
                null,
                null),
            new FinalGateDecision(
                "finding-002",
                FinalGateDecision.SummaryOnlyDisposition,
                [ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
                "cross_cutting_evidence_rules",
                [],
                null,
                "Summary-only issue."),
            new FinalGateDecision(
                "finding-003",
                FinalGateDecision.DropDisposition,
                [ReviewFindingGateReasonCodes.InvariantContradiction],
                "invariant_contradiction_rules",
                ["review_comment_message_required"],
                null,
                null),
        };

        var summary = RecordedFinalGateSummary.FromFindingsAndDecisions(findings, decisions);

        Assert.Equal(3, summary.CandidateCount);
        Assert.Equal(1, summary.PublishCount);
        Assert.Equal(1, summary.SummaryOnlyCount);
        Assert.Equal(1, summary.DropCount);
        Assert.Equal(1, summary.CategoryCounts[CandidateReviewFinding.PerFileCommentCategory]);
        Assert.Equal(2, summary.CategoryCounts[CandidateReviewFinding.CrossCuttingCategory]);
        Assert.Equal(1, summary.InvariantBlockedCount);
    }
}
