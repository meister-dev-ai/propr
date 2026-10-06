// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class PrLevelReviewVerificationExecutorTests
{
    [Fact]
    public async Task ApplyAsync_WithoutVerificationClient_UsesCollectedEvidenceAndReturnsSummaryOnlyOutcome()
    {
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);

        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new EvidenceBundle(
                    claim.ClaimId,
                    [
                        new EvidenceItem("file_content", "Foo registration", "src/Foo.cs"),
                        new EvidenceItem("file_content", "Bar registration", "src/Bar.cs"),
                    ],
                    EvidenceBundle.CompleteCoverage));

        var sut = new PrLevelReviewVerificationExecutor(extractor, collector, CreateProtocolRecorder(), new AiReviewOptions { ModelId = "fallback-model" });
        var finding = CreateSynthesizedFinding(new EvidenceReference([], ["src/Seed.cs"], EvidenceReference.MissingState, "synthesis_payload"));

        var result = await sut.ApplyAsync([finding], new ReviewSystemContext(null, [], null), "feature/x", null, null, CancellationToken.None);

        var verifiedFinding = Assert.Single(result);
        Assert.NotNull(verifiedFinding.VerificationOutcome);
        var outcome = verifiedFinding.VerificationOutcome!;
        Assert.Equal(VerificationOutcome.UnresolvedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, outcome.ReasonCodes);

        Assert.NotNull(verifiedFinding.Evidence);
        var evidence = verifiedFinding.Evidence!;
        Assert.Equal(EvidenceReference.ResolvedState, evidence.EvidenceResolutionState);
        Assert.Equal(["src/Foo.cs", "src/Bar.cs"], evidence.SupportingFiles);
        Assert.Equal("synthesis_payload", evidence.EvidenceSource);
    }

    [Fact]
    public async Task ApplyAsync_WhenClaimExtractionThrows_ReturnsDegradedFindingAndRecordsProtocolEvent()
    {
        var protocolId = Guid.NewGuid();
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(_ => throw new InvalidOperationException("claim explosion"));
        var collector = Substitute.For<IReviewEvidenceCollector>();
        var protocolRecorder = CreateProtocolRecorder();
        var sut = new PrLevelReviewVerificationExecutor(extractor, collector, protocolRecorder, new AiReviewOptions { ModelId = "fallback-model" });

        var result = await sut.ApplyAsync(
            [CreateSynthesizedFinding()], new ReviewSystemContext(null, [], null), "feature/x", protocolId, null, CancellationToken.None);

        var verifiedFinding = Assert.Single(result);
        Assert.NotNull(verifiedFinding.VerificationOutcome);
        var outcome = verifiedFinding.VerificationOutcome!;
        Assert.True(outcome.Degraded);
        Assert.Equal(VerificationOutcome.DeterministicRulesEvaluator, outcome.EvaluatedBy);
        Assert.Contains(ReviewFindingGateReasonCodes.VerificationDegraded, outcome.ReasonCodes);

        _ = collector.DidNotReceiveWithAnyArgs().CollectEvidenceAsync(default!, default, default!);
        await protocolRecorder.Received().RecordVerificationEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.VerificationDegraded),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == "claim explosion"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_ProRvOnlyFinding_UsesCrossFileEvidenceVerificationWithoutPublicationAdvantage()
    {
        ClaimDescriptor? capturedClaim = null;
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(callInfo =>
        {
            var finding = callInfo.Arg<CandidateReviewFinding>();
            capturedClaim = new ClaimDescriptor(
                $"claim-{finding.FindingId}",
                finding.FindingId,
                ClaimDescriptor.PrLevelStage,
                CandidateReviewFinding.GenericReviewAssertionClaimKind,
                finding.Message,
                finding.Severity,
                ClaimDescriptor.NeedsEvidenceMode,
                ClaimDescriptor.CodeContractFamily,
                requiresCrossFileEvidence: finding.Provenance.RequiresExplicitSupport);
            return [capturedClaim];
        });

        VerificationWorkItem? capturedWorkItem = null;
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedWorkItem = callInfo.Arg<VerificationWorkItem>();
                return new EvidenceBundle(
                    capturedWorkItem.Claim.ClaimId,
                    [new EvidenceItem("file_content", "Relevant evidence", "src/Foo.cs")],
                    EvidenceBundle.PartialCoverage);
            });

        var sut = new PrLevelReviewVerificationExecutor(extractor, collector, CreateProtocolRecorder(), new AiReviewOptions { ModelId = "fallback-model" });
        var finding = new CandidateReviewFinding(
            "finding-prorv-only",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "late_steering_merge",
                "src/Foo.cs",
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: FindingProvenanceKind.ProRVOnly),
            CommentSeverity.Warning,
            "ProRV-only issue needs stronger support.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            20);

        var result = await sut.ApplyAsync([finding], new ReviewSystemContext(null, [], null), "feature/x", null, null, CancellationToken.None);

        Assert.NotNull(capturedClaim);
        Assert.True(capturedClaim!.RequiresCrossFileEvidence);
        Assert.NotNull(capturedWorkItem);
        Assert.True(capturedWorkItem!.FindingProvenance.RequiresExplicitSupport);

        var verifiedFinding = Assert.Single(result);
        Assert.NotNull(verifiedFinding.VerificationOutcome);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, verifiedFinding.VerificationOutcome!.RecommendedDisposition);
    }

    [Fact]
    public async Task ApplyAsync_WhenVerifierRuns_PreservesScopeRelationOnRebuiltFinding()
    {
        // The executor rebuilds each finding to attach its VerificationOutcome. The changed-line ScopeRelation must
        // survive that rebuild: dropping it strips the anchor the PR-wide gate needs to publish, which silently
        // demotes every verified PR-wide finding to summary-only. This exercises the real rebuild path (>=1 claim,
        // evidence collected, verifier invoked) rather than the empty-claim short-circuit that returns the input
        // untouched — the NotNull outcome and NotSame assertions below confirm the finding was reconstructed.
        var claim = CreateClaim("finding-scope-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);

        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new EvidenceBundle(
                    claim.ClaimId,
                    [
                        new EvidenceItem("file_content", "Foo registration", "src/Foo.cs"),
                        new EvidenceItem("file_content", "Bar registration", "src/Bar.cs"),
                    ],
                    EvidenceBundle.CompleteCoverage));

        var sut = CreateJudgedExecutor(extractor, collector, CreateProtocolRecorder());
        var reviewContext = JudgeContext(JudgeAnswering("{\"verdict\":\"confirmed\",\"reason\":\"both files lack the registration\"}"));

        var finding = new CandidateReviewFinding(
            "finding-scope-001",
            new CandidateFindingProvenance(CandidateFindingProvenance.PrWidePassOrigin, "pr_wide_pass"),
            CommentSeverity.Warning,
            "Cross-file registration is missing on a changed line.",
            CandidateReviewFinding.CrossCuttingCategory,
            "src/Foo.cs",
            12,
            new EvidenceReference([], ["src/Foo.cs"], EvidenceReference.PartialState, "pr_wide_synthesis"),
            "Potential cross-file registration gap.",
            scopeRelation: ChangedLineRelation.OnChangedLine);

        var result = await sut.ApplyAsync([finding], reviewContext, "feature/x", null, null, CancellationToken.None);

        var verifiedFinding = Assert.Single(result);
        Assert.Equal(ChangedLineRelation.OnChangedLine, verifiedFinding.ScopeRelation);
        Assert.NotNull(verifiedFinding.VerificationOutcome);
        Assert.Equal(FinalGateDecision.PublishDisposition, verifiedFinding.VerificationOutcome!.RecommendedDisposition);
        Assert.NotSame(finding, verifiedFinding);
    }

    [Fact]
    public async Task ApplyAsync_ThePrLevelClaimIsDecidedByTheEvidenceJudge_AndRecordedLikeAFileFinding()
    {
        var protocolId = Guid.NewGuid();
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);
        var recorder = CreateProtocolRecorder();
        var judge = JudgeAnswering("{\"verdict\":\"confirmed\",\"reason\":\"Foo and Bar both miss the registration\"}");

        var result = await CreateJudgedExecutor(extractor, CollectorWithTwoFiles(claim), recorder)
            .ApplyAsync([CreateSynthesizedFinding()], JudgeContext(judge), "feature/x", protocolId, null, CancellationToken.None);

        var outcome = Assert.Single(result).VerificationOutcome!;
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeVerdicts.Confirmed, outcome.JudgeVerdict);
        Assert.Equal(VerificationOutcome.AiMicroVerifierEvaluator, outcome.EvaluatedBy);
        var callNames = recorder.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IProtocolRecorder.RecordAiCallAsync))
            .Select(call => (string?)call.GetArguments()[8])
            .ToList();
        Assert.Equal(["ai_call_evidence_verification"], callNames);
    }

    [Theory]
    [InlineData("{\"verdict\":\"not_confirmed\",\"reason\":\"Bar registers the service\"}")]
    [InlineData("{\"verdict\":\"not_actionable\",\"reason\":\"no caller resolves the service\"}")]
    [InlineData("{\"verdict\":\"intended\",\"reason\":\"the description states it\"}")]
    [InlineData("not json")]
    public async Task ApplyAsync_WhenTheJudgeDoesNotConfirm_ThePrLevelFindingIsWithheld(string answer)
    {
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);

        var result = await CreateJudgedExecutor(extractor, CollectorWithTwoFiles(claim), CreateProtocolRecorder())
            .ApplyAsync([CreateSynthesizedFinding()], JudgeContext(JudgeAnswering(answer)), "feature/x", null, null, CancellationToken.None);

        Assert.NotEqual(FinalGateDecision.PublishDisposition, Assert.Single(result).VerificationOutcome!.RecommendedDisposition);
    }

    [Fact]
    public async Task ApplyAsync_AFindingWithoutAClaim_IsWithheld()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([]);
        var judge = JudgeAnswering("{\"verdict\":\"confirmed\",\"reason\":\"x\"}");

        var result = await CreateJudgedExecutor(extractor, Substitute.For<IReviewEvidenceCollector>(), CreateProtocolRecorder())
            .ApplyAsync([CreateSynthesizedFinding()], JudgeContext(judge), "feature/x", null, null, CancellationToken.None);

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, Assert.Single(result).VerificationOutcome!.RecommendedDisposition);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task ApplyAsync_AnUndecidedSynthesizedFinding_IsWithheld_AndAnUndecidedPrWidePassFinding_IsPublished()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(callInfo => [CreateClaim(callInfo.Arg<CandidateReviewFinding>().FindingId)]);
        var judge = JudgeAnswering("{\"verdict\":\"insufficient_context\",\"reason\":\"the registration code is not shown\"}");
        var synthesized = CreateSynthesizedFinding();
        var prWide = CreatePrWidePassFinding("finding-prw-01-001");

        var result = await CreateJudgedExecutor(extractor, CollectorWithTwoFiles(CreateClaim("finding-001")), CreateProtocolRecorder())
            .ApplyAsync([synthesized, prWide], JudgeContext(judge), "feature/x", null, null, CancellationToken.None);

        // The synthesis call reads no repository code, so its undecided finding is withheld; the PR-wide pass
        // investigated with the review tools, so its undecided finding is published.
        Assert.Equal(["finding-001", "finding-prw-01-001"], result.Select(finding => finding.FindingId));
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, result[0].VerificationOutcome!.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeVerdicts.InsufficientContext, result[0].VerificationOutcome!.JudgeVerdict);
        Assert.Equal(FinalGateDecision.PublishDisposition, result[1].VerificationOutcome!.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeVerdicts.InsufficientContext, result[1].VerificationOutcome!.JudgeVerdict);
    }

    [Fact]
    public async Task ApplyAsync_JudgesAllClaimsInOneVerifierCall_AndKeepsTheFindingOrder()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(callInfo =>
        {
            var findingId = callInfo.Arg<CandidateReviewFinding>().FindingId;
            return findingId == "finding-002" ? [] : [CreateClaim(findingId)];
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        IReadOnlyList<VerificationWorkItem>? judged = null;

        // The verifier answers in reverse order, so the outcomes have to be matched to the findings by claim.
        verifier.VerifyAsync(
                Arg.Do<IReadOnlyList<VerificationWorkItem>>(items => judged = items),
                Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<IReadOnlyList<VerificationWorkItem>>()
                .Reverse()
                .Select(item => new VerificationOutcome(
                    item.Claim.ClaimId,
                    item.Claim.FindingId,
                    VerificationOutcome.SupportedKind,
                    FinalGateDecision.PublishDisposition,
                    [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                    [],
                    VerificationOutcome.ModerateEvidence,
                    $"decided {item.Claim.FindingId}",
                    VerificationOutcome.AiMicroVerifierEvaluator,
                    false))
                .ToList());
        var sut = new PrLevelReviewVerificationExecutor(
            extractor, CollectorWithTwoFiles(CreateClaim("finding-001")), CreateProtocolRecorder(), new AiReviewOptions { ModelId = "fallback-model" },
            verifier);
        var findings = new[] { CreatePrWidePassFinding("finding-001"), CreatePrWidePassFinding("finding-002"), CreatePrWidePassFinding("finding-003") };

        var result = await sut.ApplyAsync(findings, JudgeContext(JudgeAnswering("unused")), "feature/x", null, null, CancellationToken.None);

        await verifier.ReceivedWithAnyArgs(1).VerifyAsync(default!, default!, default, default);
        Assert.Equal(["claim-finding-001", "claim-finding-003"], judged!.Select(item => item.Claim.ClaimId));
        Assert.Equal(["finding-001", "finding-002", "finding-003"], result.Select(finding => finding.FindingId));
        Assert.Equal("decided finding-001", result[0].VerificationOutcome!.EvidenceSummary);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, result[1].VerificationOutcome!.RecommendedDisposition);
        Assert.Equal("decided finding-003", result[2].VerificationOutcome!.EvidenceSummary);
    }

    [Fact]
    public async Task ApplyAsync_TheJudgeReceivesTheSupportingFilesOfAClaimWithoutAnAnchorFile()
    {
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);
        List<ChatMessage>? messages = null;
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => messages = m.ToList()), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"Bar misses AddFoo\"}")));

        await CreateJudgedExecutor(extractor, CollectorWithTwoFiles(claim), CreateProtocolRecorder())
            .ApplyAsync([CreateSynthesizedFinding()], JudgeContext(judge), "feature/x", null, null, CancellationToken.None);

        var user = messages![1].Text;
        Assert.Contains("FILES THE FINDING CITES", user, StringComparison.Ordinal);
        Assert.Contains("--- src/Foo.cs, lines 1-1 ---\nservices.AddFoo();", user, StringComparison.Ordinal);
        Assert.Contains("--- src/Bar.cs, lines 1-1 ---\nservices.AddFoo();", user, StringComparison.Ordinal);
    }

    private static PrLevelReviewVerificationExecutor CreateJudgedExecutor(
        IReviewClaimExtractor extractor,
        IReviewEvidenceCollector collector,
        IProtocolRecorder recorder)
    {
        return new PrLevelReviewVerificationExecutor(
            extractor,
            collector,
            recorder,
            new AiReviewOptions { ModelId = "fallback-model" },
            new CompositeReviewFindingVerifier(new DeterministicLocalReviewVerifier(), new EvidenceBackedReviewVerifier(recorder)));
    }

    private static ReviewSystemContext JudgeContext(IChatClient judge)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("services.AddFoo();");
        return new ReviewSystemContext(null, [], tools)
        {
            DefaultReviewChatClient = judge,
            DefaultReviewModelId = "judge-model",
        };
    }

    private static IChatClient JudgeAnswering(string answer)
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        return judge;
    }

    private static IReviewEvidenceCollector CollectorWithTwoFiles(ClaimDescriptor claim)
    {
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new EvidenceBundle(
                    claim.ClaimId,
                    [
                        new EvidenceItem("file_content", "Foo registration", "src/Foo.cs"),
                        new EvidenceItem("file_content", "Bar registration", "src/Bar.cs"),
                    ],
                    EvidenceBundle.CompleteCoverage));
        return collector;
    }

    private static ClaimDescriptor CreateClaim(string findingId)
    {
        return new ClaimDescriptor(
            $"claim-{findingId}",
            findingId,
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file registration is missing.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);
    }

    private static CandidateReviewFinding CreateSynthesizedFinding(EvidenceReference? evidence = null)
    {
        return new CandidateReviewFinding(
            "finding-001",
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
            CommentSeverity.Warning,
            "Cross-file registration is missing.",
            CandidateReviewFinding.CrossCuttingCategory,
            evidence: evidence,
            candidateSummaryText: "Potential cross-file registration gap.");
    }

    private static CandidateReviewFinding CreatePrWidePassFinding(string findingId)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PrWidePassOrigin,
                "pr_wide_pass",
                reviewPassKind: ReviewPassKind.MultiPassUnion,
                unionPassIndex: 1),
            CommentSeverity.Warning,
            "Cross-file registration is missing.",
            CandidateReviewFinding.CrossCuttingCategory,
            candidateSummaryText: "Potential cross-file registration gap.");
    }

    private static IProtocolRecorder CreateProtocolRecorder()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        recorder.AddTokensAsync(
                Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }
}
