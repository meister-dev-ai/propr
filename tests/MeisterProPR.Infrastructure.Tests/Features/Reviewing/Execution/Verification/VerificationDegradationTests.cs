// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class VerificationDegradationTests
{
    [Fact]
    public async Task DeterministicLocalReviewVerifier_WhenInvariantInspectionThrows_ReturnsDegradedSummaryOnlyOutcome()
    {
        var sut = new DeterministicLocalReviewVerifier();
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.CodeContractFamily);

        var outcomes = await sut.VerifyAsync(
            [
                new VerificationWorkItem(
                    claim,
                    new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
                    ClaimDescriptor.LocalStage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false),
            ],
            new ThrowingInvariantFactList(),
            ct: CancellationToken.None);
        var outcome = outcomes[0];

        Assert.True(outcome.Degraded);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerificationDegraded, outcome.ReasonCodes);
    }

    [Fact]
    public void VerificationOutcome_DegradedUnresolved_ReturnsSummaryOnlyDegradedOutcome()
    {
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file claim.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);

        var outcome = VerificationOutcome.DegradedUnresolved(
            claim,
            VerificationOutcome.AiMicroVerifierEvaluator,
            ReviewFindingGateReasonCodes.VerificationDegraded,
            "AI micro-verification degraded: Evidence unreadable.");

        Assert.True(outcome.Degraded);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerificationDegraded, outcome.ReasonCodes);
    }

    [Theory]
    [InlineData(VerificationOutcome.UnresolvedKind)]
    [InlineData(VerificationOutcome.InsufficientEvidenceKind)]
    [InlineData(VerificationOutcome.NonVerifiableKind)]
    public void VerificationOutcome_ExplicitConservativeOutcomes_RemainSummaryOnly(string outcomeKind)
    {
        var outcome = new VerificationOutcome(
            "claim-1",
            "finding-1",
            outcomeKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            "Claim did not reach bounded support.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        Assert.Equal(outcomeKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.False(outcome.BlocksPublication);
    }

    [Fact]
    public async Task DeterministicLocalReviewVerifier_GenericApiUsageClaim_ReturnsNonVerifiableSummaryOnlyOutcome()
    {
        var sut = new DeterministicLocalReviewVerifier();
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.GenericReviewAssertionClaimKind,
            "The helper method isCommentRelevanceEvent is missing and will fail at runtime.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.ApiOrSymbolUsageFamily,
            requiresSymbolEvidence: true);

        var outcomes = await sut.VerifyAsync(
            [
                new VerificationWorkItem(
                    claim,
                    new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
                    ClaimDescriptor.LocalStage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false),
            ],
            [],
            ct: CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.NonVerifiableKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, outcome.ReasonCodes);
        Assert.False(outcome.BlocksPublication);
    }

    [Fact]
    public async Task DeterministicLocalReviewVerifier_AgenticObjectiveClaim_ReturnsSupportedPublishOutcome()
    {
        var sut = new DeterministicLocalReviewVerifier();
        var claim = new ClaimDescriptor(
            "claim-obj-1",
            "finding-obj-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.DockerFinalStageRootUserClaimKind,
            "The final Docker stage runs as root because a runtime USER directive is missing.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.OperationalRiskFamily,
            anchorFilePath: "Dockerfile",
            anchorLineNumber: 8);

        var outcomes = await sut.VerifyAsync(
            [
                new VerificationWorkItem(
                    claim,
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.DeeperFollowUpOrigin,
                        "agentic_file_investigation",
                        "Dockerfile",
                        evidenceSetId: "evidence-docker-001",
                        requiresExplicitSupport: true,
                        sourceOriginId: "task-001"),
                    ClaimDescriptor.LocalStage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false),
            ],
            [],
            ct: CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.SupportedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, outcome.ReasonCodes);
    }

    [Fact]
    public async Task DeterministicLocalReviewVerifier_ProRvOnlyGenericClaim_RemainsConservativeSummaryOnly()
    {
        var sut = new DeterministicLocalReviewVerifier();
        var claim = new ClaimDescriptor(
            "claim-prorv-1",
            "finding-prorv-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.GenericReviewAssertionClaimKind,
            "ProRV-only issue needs stronger support.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CodeContractFamily);

        var outcomes = await sut.VerifyAsync(
            [
                new VerificationWorkItem(
                    claim,
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.PerFileCommentOrigin,
                        "late_steering_merge",
                        "src/Foo.cs",
                        requiresExplicitSupport: true,
                        reviewPassKind: ReviewPassKind.ProRVAugmentation,
                        findingProvenanceKind: FindingProvenanceKind.ProRVOnly),
                    ClaimDescriptor.LocalStage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false),
            ],
            [],
            ct: CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.NonVerifiableKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, outcome.ReasonCodes);
        Assert.False(outcome.BlocksPublication);
    }

    [Fact]
    public async Task DeterministicLocalReviewVerifier_ProRvOnlyObjectiveClaim_StillRequiresBoundedSupport()
    {
        var sut = new DeterministicLocalReviewVerifier();
        var claim = new ClaimDescriptor(
            "claim-prorv-obj-1",
            "finding-prorv-obj-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.DockerFinalStageRootUserClaimKind,
            "The final Docker stage runs as root because a runtime USER directive is missing.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.OperationalRiskFamily,
            anchorFilePath: "Dockerfile",
            anchorLineNumber: 8);

        var outcomes = await sut.VerifyAsync(
            [
                new VerificationWorkItem(
                    claim,
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.PerFileCommentOrigin,
                        "late_steering_merge",
                        "Dockerfile",
                        requiresExplicitSupport: true,
                        reviewPassKind: ReviewPassKind.ProRVAugmentation,
                        findingProvenanceKind: FindingProvenanceKind.ProRVOnly),
                    ClaimDescriptor.LocalStage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false),
            ],
            [],
            ct: CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.NonVerifiableKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, outcome.ReasonCodes);
        Assert.False(outcome.BlocksPublication);
    }

    private sealed class ThrowingInvariantFactList : List<InvariantFact>, IReadOnlyList<InvariantFact>
    {
        public new IEnumerator<InvariantFact> GetEnumerator()
        {
            throw new InvalidOperationException("Invariant source unavailable.");
        }
    }
}
