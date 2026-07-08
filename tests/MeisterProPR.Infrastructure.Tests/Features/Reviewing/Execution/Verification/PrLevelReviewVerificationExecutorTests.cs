// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

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
    public async Task ApplyAsync_WhenAiReturnsMarkdownFencedJson_ParsesOutcomeAndRecordsTokens()
    {
        var protocolId = Guid.NewGuid();
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);

        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new EvidenceBundle(
                    claim.ClaimId,
                    [new EvidenceItem("file_content", "Foo registration", "src/Foo.cs")],
                    EvidenceBundle.PartialCoverage));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "```json\n{\"verdict\":\"supported\",\"recommended_disposition\":\"Publish\",\"summary\":\"Repository evidence confirms the claim.\"}\n```"))
                {
                    Usage = new UsageDetails { InputTokenCount = 41, OutputTokenCount = 13 },
                });

        var protocolRecorder = CreateProtocolRecorder();
        var sut = new PrLevelReviewVerificationExecutor(extractor, collector, protocolRecorder, new AiReviewOptions { ModelId = "fallback-model" });
        var reviewContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "micro-model",
            Temperature = 0.33f,
        };

        var result = await sut.ApplyAsync([CreateSynthesizedFinding()], reviewContext, "feature/x", protocolId, null, CancellationToken.None);

        var verifiedFinding = Assert.Single(result);
        Assert.NotNull(verifiedFinding.VerificationOutcome);
        var outcome = verifiedFinding.VerificationOutcome!;
        Assert.Equal(VerificationOutcome.SupportedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, outcome.ReasonCodes);
        Assert.Equal(VerificationOutcome.StrongEvidence, outcome.EvidenceStrength);
        Assert.False(outcome.Degraded);

        await protocolRecorder.Received().AddTokensAsync(
            protocolId,
            41,
            13,
            AiConnectionModelCategory.Default,
            "micro-model",
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordAiCallAsync(
            protocolId,
            0,
            41,
            13,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains("\"verdict\":\"supported\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(),
            "ai_call_pr_verification");
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
    public async Task ApplyAsync_WithPromptExperiment_UsesVariantVerificationMessages()
    {
        var protocolId = Guid.NewGuid();
        var claim = CreateClaim("finding-001");
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([claim]);

        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new EvidenceBundle(
                    claim.ClaimId,
                    [new EvidenceItem("file_content", "Foo registration", "src/Foo.cs")],
                    EvidenceBundle.PartialCoverage));

        List<ChatMessage>? capturedMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(messages => capturedMessages = messages.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{" +
                        "\"verdict\":\"supported\"," +
                        "\"recommended_disposition\":\"Publish\"," +
                        "\"summary\":\"Repository evidence confirms the claim.\"}")));

        var promptExperiment = new PromptExperimentContext(
            "variant-a",
            [
                new StagePromptVariant(
                    PromptStageKeys.PrVerificationSystem, PromptStageRole.System, PromptCompositionMode.Prepend, "Variant verification system"),
                new StagePromptVariant(PromptStageKeys.PrVerificationUser, PromptStageRole.User, PromptCompositionMode.Replace, "Variant verification user"),
            ]);

        var protocolRecorder = CreateProtocolRecorder();
        var sut = new PrLevelReviewVerificationExecutor(extractor, collector, protocolRecorder, new AiReviewOptions { ModelId = "fallback-model" });
        var reviewContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "micro-model",
            PromptExperiment = promptExperiment,
        };

        var result = await sut.ApplyAsync([CreateSynthesizedFinding()], reviewContext, "feature/x", protocolId, null, CancellationToken.None);

        var verifiedFinding = Assert.Single(result);
        Assert.Equal(FinalGateDecision.PublishDisposition, verifiedFinding.VerificationOutcome!.RecommendedDisposition);
        Assert.NotNull(capturedMessages);
        var systemMessage = Assert.Single(capturedMessages!, message => message.Role == ChatRole.System);
        var userMessage = Assert.Single(capturedMessages, message => message.Role == ChatRole.User);
        Assert.StartsWith("Variant verification system", systemMessage.Text, StringComparison.Ordinal);
        Assert.Equal("Variant verification user", userMessage.Text);
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
