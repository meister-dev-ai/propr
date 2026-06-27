// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class CompositeReviewFindingVerifierTests
{
    [Fact]
    public async Task WhenFlagDisabled_PreservesDeterministicWithhold()
    {
        var sut = CreateSut(false);

        var outcomes = await sut.VerifyAsync(
            [CreateWithheldWorkItem()],
            [],
            new ReviewVerificationContext(null, "source", null, null),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.NonVerifiableKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(VerificationOutcome.DeterministicRulesEvaluator, outcome.EvaluatedBy);
    }

    [Fact]
    public async Task WhenFlagEnabledButNoEvidenceChannel_PreservesDeterministicWithhold()
    {
        var sut = CreateSut(true);

        // Tools null → no way to gather evidence → the composite must not escalate, leaving the
        // conservative deterministic outcome untouched.
        var outcomes = await sut.VerifyAsync(
            [CreateWithheldWorkItem()],
            [],
            new ReviewVerificationContext(null, "source", null, null),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(VerificationOutcome.DeterministicRulesEvaluator, outcome.EvaluatedBy);
    }

    private static CompositeReviewFindingVerifier CreateSut(bool enabled)
    {
        return new CompositeReviewFindingVerifier(
            new DeterministicLocalReviewVerifier(),
            new EvidenceBackedReviewVerifier(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { EnableEvidenceBackedVerification = enabled }));
    }

    private static VerificationWorkItem CreateWithheldWorkItem()
    {
        // A symbol-evidence-requiring claim is the class the deterministic verifier can only withhold
        // (NonVerifiable / SummaryOnly) because it cannot read code to substantiate it.
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "The method `lookup` dereferences a value that may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.ApiOrSymbolUsageFamily,
            requiresSymbolEvidence: true);

        return new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
            ClaimDescriptor.LocalStage,
            VerificationWorkItem.AnchorOnlyScope,
            false);
    }
}
