// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class DeterministicReviewFindingGateTests
{
    private static CandidateReviewFinding CreateFinding(
        string findingId,
        string message,
        string category,
        EvidenceReference? evidence = null,
        string? candidateSummaryText = null,
        string? filePath = null,
        int? lineNumber = null,
        VerificationOutcome? verificationOutcome = null)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                category == CandidateReviewFinding.CrossCuttingCategory
                    ? CandidateFindingProvenance.SynthesizedCrossCuttingOrigin
                    : CandidateFindingProvenance.PerFileCommentOrigin,
                category == CandidateReviewFinding.CrossCuttingCategory ? "synthesis" : "per_file_review",
                filePath),
            CommentSeverity.Warning,
            message,
            category,
            filePath,
            lineNumber,
            evidence,
            candidateSummaryText,
            verificationOutcome: verificationOutcome);
    }

    [Fact]
    public async Task EvaluateAsync_CrossCuttingWithoutResolvedMultiFileEvidence_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-cc-001",
            "Potential cross-cutting concern.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], [], EvidenceReference.MissingState, "synthesis_payload"),
            "Potential cross-cutting concern noted, but the available evidence did not support publication as a review thread.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingMultiFileEvidence, decisions[0].ReasonCodes);
        Assert.Equal("cross_cutting_evidence_rules", decisions[0].RuleSource);
        Assert.NotNull(decisions[0].SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_CrossCuttingWithResolvedMultiFileEvidence_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-cc-001",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference(
                ["finding-pf-001", "finding-pf-002"],
                ["src/Foo.cs", "src/Bar.cs"],
                EvidenceReference.ResolvedState,
                "synthesis_payload"),
            "Potential DI registration gap spans multiple files.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, decisions[0].ReasonCodes);
        Assert.Equal("cross_cutting_claim_support_rules", decisions[0].RuleSource);
        Assert.NotNull(decisions[0].SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_BroadWeakArchitectureFinding_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-arch-001",
            "Architecture concerns span the PR and should be revisited.",
            "architecture",
            null,
            "Potential architecture concern noted, but it did not meet the publication bar for an actionable review thread.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.WeakBroadFinding, decisions[0].ReasonCodes);
        Assert.Equal("broad_finding_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_NonActionableFinding_AssignsDrop()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-na-001",
            "Consider refactoring this area for clarity.",
            "non_actionable");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.NonActionableFinding, decisions[0].ReasonCodes);
        Assert.Equal("non_actionable_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_VerificationOutcomeSummaryOnly_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-verify-001",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Missing DI registration in multiple files.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            claimFamily: ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);

        var outcome = new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.UnresolvedKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
            [],
            VerificationOutcome.WeakEvidence,
            "Evidence collection did not independently support publication.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        var finding = CreateFinding(
            "finding-verify-001",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.MissingState, "synthesis_payload"),
            "Potential DI registration gap spans multiple files.",
            verificationOutcome: outcome);

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingMultiFileEvidence, decisions[0].ReasonCodes);
        Assert.Equal("verification_outcome_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_VerificationOutcomePublish_AssignsPublish()
    {
        var sut = new DeterministicReviewFindingGate();
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-verify-002",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Missing DI registration in multiple files.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            claimFamily: ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);

        var outcome = new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.StrongEvidence,
            "Bounded claim support was independently verified.",
            VerificationOutcome.CombinedEvaluator,
            false);

        var finding = CreateFinding(
            "finding-verify-002",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "synthesis_payload"),
            "Potential DI registration gap spans multiple files.",
            verificationOutcome: outcome);

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.PublishDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, decisions[0].ReasonCodes);
        Assert.Equal("verification_outcome_rules", decisions[0].RuleSource);
    }
}
