// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class WithheldReviewFindingRetentionTests
{
    [Fact]
    public async Task LocalVerification_MixedClaimOutcomesRetainOneCandidateWithTheBlockingReason()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call =>
        {
            var finding = call.Arg<CandidateReviewFinding>();
            return [Claim("supported", finding), Claim("blocked", finding)];
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
            Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
            Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var items = call.Arg<IReadOnlyList<VerificationWorkItem>>();
            return Task.FromResult<IReadOnlyList<VerificationOutcome>>(
            [
                Outcome(items[0].Claim, FinalGateDecision.PublishDisposition),
                Outcome(items[1].Claim, FinalGateDecision.SummaryOnlyDisposition) with { JudgeDegradation = EvidenceJudgeDegradations.OutputLimit },
            ]);
        });
        var sut = new LocalReviewVerificationExecutor(extractor, verifier, Substitute.For<IProtocolRecorder>());
        var comment = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential defect.");

        var result = await sut.ApplyDetailedAsync(
            new ReviewResult("Summary", [comment]),
            new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), null, [], null, null, CancellationToken.None);

        Assert.Empty(result.Result.Comments);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, Assert.Single(result.Result.WithheldFindings).ReasonCode);
        Assert.Equal("blocked", Assert.Single(result.VerifiedCandidateFindings).VerificationOutcome!.ClaimId);
    }

    [Fact]
    public async Task LocalVerification_RetainsDegradedCandidateWithoutPublishingIt()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call =>
        {
            var finding = call.Arg<CandidateReviewFinding>();
            return
            [
                new ClaimDescriptor(
                    "claim", finding.FindingId, ClaimDescriptor.LocalStage,
                    CandidateReviewFinding.GenericReviewAssertionClaimKind, finding.Message, finding.Severity,
                    ClaimDescriptor.NeedsEvidenceMode, ClaimDescriptor.OperationalRiskFamily)
            ];
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
            Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
            Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var claim = call.Arg<IReadOnlyList<VerificationWorkItem>>()[0].Claim;
            return Task.FromResult<IReadOnlyList<VerificationOutcome>>(
            [
                new VerificationOutcome(
                    claim.ClaimId,
                    claim.FindingId, VerificationOutcome.NonVerifiableKind, FinalGateDecision.SummaryOnlyDisposition,
                    [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport], [], VerificationOutcome.NoEvidence,
                    "Judge output budget exhausted.", VerificationOutcome.AiMicroVerifierEvaluator, true)
                {
                    JudgeDegradation = EvidenceJudgeDegradations.OutputLimit,
                }
            ]);
        });
        var comment = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential null dereference.")
        {
            OriginModelId = "review-model",
            OriginPassKind = "Baseline",
        };
        var sut = new LocalReviewVerificationExecutor(extractor, verifier, Substitute.For<IProtocolRecorder>());

        var result = await sut.ApplyAsync(
            new ReviewResult("Summary", [comment]),
            new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), null, [], null, CancellationToken.None);

        Assert.Empty(result.Comments);
        var withheld = Assert.Single(result.WithheldFindings);
        Assert.Equal(comment, withheld.Comment);
        Assert.Equal(EvidenceJudgeDegradations.OutputLimit, withheld.ReasonCode);
    }

    private static ClaimDescriptor Claim(string id, CandidateReviewFinding finding) => new(
        id, finding.FindingId,
        ClaimDescriptor.LocalStage, CandidateReviewFinding.GenericReviewAssertionClaimKind, finding.Message, finding.Severity,
        ClaimDescriptor.NeedsEvidenceMode, ClaimDescriptor.CodeContractFamily);

    private static VerificationOutcome Outcome(ClaimDescriptor claim, string disposition) => new(
        claim.ClaimId, claim.FindingId,
        VerificationOutcome.UnresolvedKind, disposition, [], [], VerificationOutcome.NoEvidence, null, VerificationOutcome.AiMicroVerifierEvaluator, false);
}
