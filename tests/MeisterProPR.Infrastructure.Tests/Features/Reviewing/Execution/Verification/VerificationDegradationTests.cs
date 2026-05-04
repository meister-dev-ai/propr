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
            claimFamily: ClaimDescriptor.CodeContractFamily);

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
            CancellationToken.None);
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
            claimFamily: ClaimDescriptor.CrossFileConsistencyFamily,
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
            claimFamily: ClaimDescriptor.ApiOrSymbolUsageFamily,
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
            CancellationToken.None);

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
