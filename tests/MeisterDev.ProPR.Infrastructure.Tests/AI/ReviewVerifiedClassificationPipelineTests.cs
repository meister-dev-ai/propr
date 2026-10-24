// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewVerifiedClassificationPipelineTests
{
    [Theory]
    [InlineData("defect", CommentSeverity.Error)]
    [InlineData("improvement", CommentSeverity.Suggestion)]
    [InlineData(null, CommentSeverity.Suggestion)]
    public async Task LocalVerification_PreservesClaimAndFixAndClassifiesConfirmedDefects(string? kind, CommentSeverity expected)
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call =>
        {
            var finding = call.Arg<CandidateReviewFinding>();
            return
            [
                new ClaimDescriptor(
                    "claim", finding.FindingId, ClaimDescriptor.LocalStage, CandidateReviewFinding.GenericReviewAssertionClaimKind,
                    finding.Message, finding.Severity, ClaimDescriptor.DeterministicOnlyMode, ClaimDescriptor.CodeContractFamily)
            ];
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<VerificationOutcome>>(
                call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item =>
                    new VerificationOutcome(
                        item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.SupportedKind, FinalGateDecision.PublishDisposition,
                        [], [], VerificationOutcome.ModerateEvidence, "Confirmed source mechanism.", VerificationOutcome.AiMicroVerifierEvaluator, false)
                    {
                        JudgeVerdict = EvidenceJudgeVerdicts.Confirmed,
                        VerifiedFindingKind = kind,
                    }).ToList()));
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Suggestion, "If the preview feature is enabled, this guard fails.")
        {
            SuggestedFix = new ReviewSuggestedFix(2, 2, "old();", "fixed();"),
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Low, FindingLikelihood.Unlikely, "One retry fails.", "Requires preview.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The generator claimed certainty."),
        };
        var actual = await new LocalReviewVerificationExecutor(extractor, verifier, Substitute.For<IProtocolRecorder>())
            .ApplyDetailedAsync(
                new ReviewResult("Conditional source summary.", [finding]), new ReviewFileResult(Guid.NewGuid(), "a.cs"), null, [], null, null,
                CancellationToken.None);
        var kept = Assert.Single(actual.Result.Comments);
        var verified = Assert.Single(actual.VerifiedCandidateFindings);
        Assert.Equal(expected, kept.Severity);
        Assert.Equal(expected, verified.Severity);
        Assert.NotNull(verified.VerificationOutcome);
        Assert.Equal("claim", verified.VerificationOutcome.ClaimId);
        Assert.Equal(verified.FindingId, verified.VerificationOutcome.FindingId);
        Assert.Equal(finding.Message, kept.Message);
        Assert.Equal(finding.SuggestedFix, kept.SuggestedFix);
        Assert.NotNull(kept.RiskAssessment);
        Assert.Equal(finding.RiskAssessment.Impact, kept.RiskAssessment.Impact);
        Assert.Equal(finding.RiskAssessment.Likelihood, kept.RiskAssessment.Likelihood);
        Assert.Equal(finding.RiskAssessment.ImpactReason, kept.RiskAssessment.ImpactReason);
        Assert.Equal(finding.RiskAssessment.LikelihoodReason, kept.RiskAssessment.LikelihoodReason);
        Assert.Equal(finding.RiskAssessment.Source, kept.RiskAssessment.Source);
        Assert.Equal(FindingConfidence.Unknown, kept.RiskAssessment.Confidence);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, kept.RiskAssessment.ConfidenceSource);
        Assert.Equal(kept.RiskAssessment, verified.RiskAssessment);
        Assert.Null(kept.ProducedWithRepositoryTools);
    }
}
