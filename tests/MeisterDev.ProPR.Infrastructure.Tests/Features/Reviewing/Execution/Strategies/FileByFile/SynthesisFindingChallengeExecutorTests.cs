// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

public sealed class SynthesisFindingChallengeExecutorTests
{
    [Fact]
    public async Task Reconsideration_RefutesOnlyTheChallengedFindingAndRemovesItsSummaryClaim()
    {
        var before = Finding("finding-1", "The retry loop ignores the latest allocation.");
        var untouched = Finding("finding-2", "The reachable null input throws in Save.");
        IReadOnlyList<VerificationWorkItem>? seen = null;
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<IReadOnlyList<VerificationWorkItem>>();
                return seen.Select(item => new VerificationOutcome(
                    item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.ContradictedKind,
                    FinalGateDecision.DropDisposition, ["source_counterevidence_refutes_claim"], [], VerificationOutcome.StrongEvidence,
                    "Math.Min updates smallest with every run; [120,80] stops at two runs.", VerificationOutcome.AiMicroVerifierEvaluator, false)).ToArray();
            });
        var recorder = Substitute.For<IProtocolRecorder>();
        var verifier = Verifier(judge, recorder);
        var challenge = new FindingVerificationChallenge(before.FindingId, "The stated mechanism needs reconsideration.", "Math.Min updates the accumulator.");
        var sut = new SynthesisFindingChallengeExecutor(verifier, recorder);

        var candidates = await sut.ApplyAsync(
            [before, untouched], [challenge, challenge, challenge with { FindingId = "unknown" }],
            new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>()), "source", Guid.NewGuid(), null, Guid.Empty,
            new ReviewVerificationIntent("Allocation retry", null, [], null, null), null, CancellationToken.None);
        var decisions = await new DeterministicReviewFindingGate().EvaluateAsync(candidates, []);
        var comments = ReviewSynthesisExecutor.MaterializePublishedComments(candidates, decisions);
        var summary = new SummaryReconciliationService().Reconcile(before.Message, candidates, decisions, [before.FindingId]);

        Assert.Equal(untouched.Message, Assert.Single(comments).Message);
        Assert.Equal(FinalGateDecision.DropDisposition, decisions.Single(decision => decision.FindingId == before.FindingId).Disposition);
        Assert.DoesNotContain(before.Message, summary.FinalSummary);
        Assert.Same(untouched, candidates[1]);
        var work = Assert.Single(seen!);
        Assert.Equal(challenge, work.Challenge);
        Assert.False(work.ProducedWithRepositoryTools);
        Assert.Equal(before.SuggestedFix, work.SuggestedFix);
        await judge.Received(1).VerifyAsync(
            Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
            Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>());
        await recorder.Received().RecordVerificationEventAsync(
            Arg.Any<Guid>(), "synthesis_finding_reconsideration",
            Arg.Is<string?>(text => text != null && text.Contains("unknown_finding_id", StringComparison.Ordinal)), Arg.Any<string?>(),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task MissingFailedOrSkippedReconsideration_ClearsCertaintyAndLeavesOtherFindingsPublishable(bool failure, bool skipped)
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<VerificationOutcome>>(_ => throw new InvalidOperationException("test failure"));
        var sut = new SynthesisFindingChallengeExecutor(failure || skipped ? Verifier(judge, recorder) : null, recorder);
        var candidate = Finding("finding-1", "Disputed mechanism.");
        var other = Finding("finding-2", "An independent confirmed defect.");

        var candidates = await sut.ApplyAsync(
            [candidate, other], [new FindingVerificationChallenge(candidate.FindingId, "Doubt.", "Counterevidence.")],
            new ReviewSystemContext(null, [], null) { SkippedSteps = new ReviewStepSkips(skipped ? [FileByFileReviewStepIds.PrVerification] : []) },
            "source", null, null, Guid.Empty,
            new ReviewVerificationIntent("Title", null, [], null, null), null, CancellationToken.None);
        var decisions = await new DeterministicReviewFindingGate().EvaluateAsync(candidates, []);

        Assert.Equal(FindingConfidence.Unknown, candidates[0].RiskAssessment!.Confidence);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Equal(other.Message, Assert.Single(ReviewSynthesisExecutor.MaterializePublishedComments(candidates, decisions)).Message);
        if (skipped)
        {
            await judge.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default, default);
        }
    }

    [Fact]
    public async Task LegacySupportedReconsideration_RetainsFindingWithUnassessedConfidence()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item => new VerificationOutcome(
                item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.SupportedKind, FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport], [], VerificationOutcome.ModerateEvidence,
                "Counterevidence resolved; the trigger remains reachable.", VerificationOutcome.AiMicroVerifierEvaluator, false)).ToArray());
        var candidate = Finding("finding-1", "A concrete defect.");

        var result = await new SynthesisFindingChallengeExecutor(Verifier(judge, recorder), recorder).ApplyAsync(
            [candidate],
            [new FindingVerificationChallenge(candidate.FindingId, "Check reachability.", "A guard may exclude it.")],
            new ReviewSystemContext(null, [], null), "source", null, null, Guid.Empty,
            new ReviewVerificationIntent("Title", null, [], null, null), null, CancellationToken.None);
        var decisions = await new DeterministicReviewFindingGate().EvaluateAsync(result, []);

        Assert.Equal(FindingConfidence.Unknown, result[0].RiskAssessment!.Confidence);
        Assert.Equal(CommentSeverity.Error, Assert.Single(ReviewSynthesisExecutor.MaterializePublishedComments(result, decisions)).Severity);
    }

    [Fact]
    public void Parser_BoundsAndDeduplicatesChallengesWithoutLosingSummary()
    {
        var requests = Enumerable.Range(0, 40).Select(index => new
            { finding_id = "finding-" + index, reason = "Review the state update.", counterevidence = "The accumulator is assigned." });
        var text = JsonSerializer.Serialize(new { summary = "Summary.", finding_challenges = requests });

        Assert.True(SynthesisResponseParser.TryParse(text, out var summary, out _, out _));
        Assert.Equal("Summary.", summary);
        Assert.Equal(SynthesisResponseParser.MaxFindingChallenges, SynthesisResponseParser.ParseChallenges(text).Count);
        Assert.Empty(
            SynthesisResponseParser.ParseChallenges(
                """{"summary":"ok","finding_challenges":[{"finding_id":"known","reason":5,"counterevidence":"source"}]}"""));
    }

    [Fact]
    public void Parser_CountsValidDistinctChallengesInsteadOfDuplicateOrMalformedEntries()
    {
        var entries = Enumerable.Repeat<object>(new { finding_id = "duplicate", reason = "Doubt.", counterevidence = "Source." }, 20)
            .Concat(Enumerable.Repeat<object>(new { finding_id = "invalid", reason = 1 }, 20))
            .Concat(Enumerable.Range(1, 30).Select(index => (object)new { finding_id = "finding-" + index, reason = "Doubt.", counterevidence = "Source." }));
        var parsed = SynthesisResponseParser.ParseChallenges(JsonSerializer.Serialize(new { finding_challenges = entries }));

        Assert.Equal(SynthesisResponseParser.MaxFindingChallenges, parsed.Count);
        Assert.Equal("duplicate", parsed[0].FindingId);
        Assert.Equal("finding-15", parsed[^1].FindingId);
    }

    private static CandidateReviewFinding Finding(string id, string message) => new(
        id,
        new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "a.cs"),
        CommentSeverity.Error, message, CandidateReviewFinding.PerFileCommentCategory, "a.cs", 5)
    {
        ProducedWithRepositoryTools = false,
        SuggestedFix = new ReviewSuggestedFix(5, 5, "old();", "fixed();"),
        RiskAssessment = new FindingRiskAssessment(
            FindingImpact.Low, FindingLikelihood.Likely, "Extra work.", "Ordinary input.",
            "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "Source evidence.", "evidence_verification"),
    };

    private static PrLevelReviewVerificationExecutor Verifier(IReviewFindingVerifier judge, IProtocolRecorder recorder)
    {
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new EvidenceBundle(call.Arg<VerificationWorkItem>().Claim.ClaimId, [], EvidenceBundle.PartialCoverage));
        return new PrLevelReviewVerificationExecutor(new DeterministicReviewClaimExtractor(), collector, recorder, new AiReviewOptions(), judge);
    }
}
