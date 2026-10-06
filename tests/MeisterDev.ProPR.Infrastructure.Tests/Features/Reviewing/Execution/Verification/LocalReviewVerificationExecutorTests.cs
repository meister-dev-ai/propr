// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class LocalReviewVerificationExecutorTests
{
    [Fact]
    public async Task ApplyAsync_WhenNoClaimsAreExtracted_WithholdsTheFindingAndNormalizesLineNumbers()
    {
        CandidateReviewFinding? capturedFinding = null;
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(callInfo =>
            {
                capturedFinding = callInfo.Arg<CandidateReviewFinding>();
                return [];
            });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        var sut = new LocalReviewVerificationExecutor(extractor, verifier, CreateProtocolRecorder());
        var result = new ReviewResult("summary", [new ReviewComment("src/Foo.cs", 0, CommentSeverity.Warning, "Potential issue.")]);

        var actual = await sut.ApplyAsync(result, new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), Guid.NewGuid(), [], null, CancellationToken.None);

        Assert.Empty(actual.Comments);
        Assert.NotNull(capturedFinding);
        Assert.Null(capturedFinding!.LineNumber);
        _ = verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!);
    }

    [Fact]
    public async Task ApplyAsync_WhenOutcomesWithholdFindings_RewritesSummaryAndRemovesSuppressedComments()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(callInfo =>
            {
                var finding = callInfo.Arg<CandidateReviewFinding>();
                return
                [
                    new ClaimDescriptor(
                        $"claim-{finding.FindingId}",
                        finding.FindingId,
                        ClaimDescriptor.LocalStage,
                        CandidateReviewFinding.GenericReviewAssertionClaimKind,
                        finding.Message,
                        finding.Severity,
                        ClaimDescriptor.DeterministicOnlyMode,
                        ClaimDescriptor.CodeContractFamily),
                ];
            });

        var verifier = Substitute.For<IReviewFindingVerifier>();
        var fileResult = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        var keepFindingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, 1);
        var summaryOnlyFindingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, 2);
        var droppedFindingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, 3);
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<VerificationOutcome>>(
                [
                    new VerificationOutcome(
                        $"claim-{keepFindingId}",
                        keepFindingId,
                        VerificationOutcome.SupportedKind,
                        FinalGateDecision.PublishDisposition,
                        [ReviewFindingGateReasonCodes.DefaultPublish],
                        [],
                        VerificationOutcome.StrongEvidence,
                        "Supported.",
                        VerificationOutcome.DeterministicRulesEvaluator,
                        false),
                    new VerificationOutcome(
                        $"claim-{summaryOnlyFindingId}",
                        summaryOnlyFindingId,
                        VerificationOutcome.NonVerifiableKind,
                        FinalGateDecision.SummaryOnlyDisposition,
                        [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
                        [],
                        VerificationOutcome.WeakEvidence,
                        "Needs stronger evidence.",
                        VerificationOutcome.DeterministicRulesEvaluator,
                        false),
                    new VerificationOutcome(
                        $"claim-{droppedFindingId}",
                        droppedFindingId,
                        VerificationOutcome.ContradictedKind,
                        FinalGateDecision.DropDisposition,
                        [ReviewFindingGateReasonCodes.InvariantContradiction],
                        [InvariantFact.ReviewCommentMessageRequiredInvariantId],
                        VerificationOutcome.StrongEvidence,
                        "Contradicted by invariants.",
                        VerificationOutcome.DeterministicRulesEvaluator,
                        false),
                ]));

        var sut = new LocalReviewVerificationExecutor(extractor, verifier, CreateProtocolRecorder());
        var original = new ReviewResult(
            "Original summary should be rewritten.",
            [
                new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Keep this finding."),
                new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Withhold this finding."),
                new ReviewComment("src/Foo.cs", 14, CommentSeverity.Warning, "Drop this finding."),
            ]);

        var actual = await sut.ApplyAsync(original, fileResult, null, [], null, CancellationToken.None);

        var comment = Assert.Single(actual.Comments);
        Assert.Equal("Keep this finding.", comment.Message);
        Assert.Contains("Local verification retained 1 actionable finding.", actual.Summary);
        Assert.Contains("1 candidate finding was withheld pending stronger evidence.", actual.Summary);
        Assert.Contains("1 candidate finding was dropped by deterministic verification.", actual.Summary);
        Assert.Contains("Verified local findings:", actual.Summary);
        Assert.Contains("Keep this finding.", actual.Summary);
    }

    [Fact]
    public async Task ApplyAsync_WhenClaimExtractionThrows_RecordsDegradedEventAndWithholdsTheFinding()
    {
        var protocolId = Guid.NewGuid();
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(_ => throw new InvalidOperationException("claim extraction failed"));
        var verifier = Substitute.For<IReviewFindingVerifier>();
        var protocolRecorder = CreateProtocolRecorder();
        var sut = new LocalReviewVerificationExecutor(extractor, verifier, protocolRecorder);
        var result = new ReviewResult("summary", [new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential issue.")]);

        var actual = await sut.ApplyAsync(result, new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), protocolId, [], null, CancellationToken.None);

        Assert.Empty(actual.Comments);
        _ = verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!);
        await protocolRecorder.Received().RecordVerificationEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.VerificationDegraded),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == "claim extraction failed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyDetailedAsync_EnrichedAgenticFinding_PreservesProvenanceAndVerificationOutcome()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(callInfo =>
            {
                var finding = callInfo.Arg<CandidateReviewFinding>();
                return
                [
                    new ClaimDescriptor(
                        $"claim-{finding.FindingId}",
                        finding.FindingId,
                        ClaimDescriptor.LocalStage,
                        CandidateReviewFinding.DockerFinalStageRootUserClaimKind,
                        finding.Message,
                        finding.Severity,
                        ClaimDescriptor.DeterministicOnlyMode,
                        ClaimDescriptor.OperationalRiskFamily,
                        anchorFilePath: finding.FilePath,
                        anchorLineNumber: finding.LineNumber),
                ];
            });

        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var workItems = callInfo.Arg<IReadOnlyList<VerificationWorkItem>>();
                return Task.FromResult<IReadOnlyList<VerificationOutcome>>(
                [
                    new VerificationOutcome(
                        $"claim-{workItems[0].Claim.FindingId}",
                        workItems[0].Claim.FindingId,
                        VerificationOutcome.SupportedKind,
                        FinalGateDecision.PublishDisposition,
                        [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                        [],
                        VerificationOutcome.StrongEvidence,
                        "Deterministic verifier confirmed the objective follow-up claim.",
                        VerificationOutcome.DeterministicRulesEvaluator,
                        false),
                ]);
            });

        var sut = new LocalReviewVerificationExecutor(extractor, verifier, CreateProtocolRecorder());
        var fileResult = new ReviewFileResult(Guid.NewGuid(), "Dockerfile");
        var enrichedFinding = new CandidateReviewFinding(
            "candidate-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.DeeperFollowUpOrigin,
                "agentic_file_investigation",
                "Dockerfile",
                evidenceSetId: "evidence-docker-001",
                requiresExplicitSupport: true,
                sourceOriginId: "task-001"),
            CommentSeverity.Warning,
            "The final Docker stage runs as root because a runtime USER directive is missing.",
            CandidateReviewFinding.PerFileCommentCategory,
            "Dockerfile",
            8);

        var verification = await sut.ApplyDetailedAsync(
            new ReviewResult(
                "Original summary should be rewritten.",
                [
                    new ReviewComment(
                        "Dockerfile", 8, CommentSeverity.Warning, "The final Docker stage runs as root because a runtime USER directive is missing."),
                ]),
            fileResult,
            Guid.NewGuid(),
            [],
            [enrichedFinding],
            null,
            CancellationToken.None);

        var verifiedFinding = Assert.Single(verification.VerifiedCandidateFindings);
        Assert.Equal(CandidateFindingProvenance.DeeperFollowUpOrigin, verifiedFinding.Provenance.OriginKind);
        Assert.True(verifiedFinding.Provenance.RequiresExplicitSupport);
        Assert.Equal(VerificationOutcome.SupportedKind, verifiedFinding.VerificationOutcome?.OutcomeKind);

        var publishedComment = Assert.Single(verification.Result.Comments);
        Assert.Equal("The final Docker stage runs as root because a runtime USER directive is missing.", publishedComment.Message);
    }

    [Fact]
    public async Task ApplyDetailedAsync_ProRvOnlyFinding_ElevatesExplicitSupportBeforeVerifierHandoff()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(callInfo =>
            {
                var finding = callInfo.Arg<CandidateReviewFinding>();
                return
                [
                    new ClaimDescriptor(
                        $"claim-{finding.FindingId}",
                        finding.FindingId,
                        ClaimDescriptor.LocalStage,
                        CandidateReviewFinding.GenericReviewAssertionClaimKind,
                        finding.Message,
                        finding.Severity,
                        ClaimDescriptor.DeterministicOnlyMode,
                        ClaimDescriptor.CodeContractFamily),
                ];
            });

        IReadOnlyList<VerificationWorkItem>? capturedWorkItems = null;
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedWorkItems = callInfo.Arg<IReadOnlyList<VerificationWorkItem>>();
                return Task.FromResult<IReadOnlyList<VerificationOutcome>>(
                    capturedWorkItems
                        .Select(item => new VerificationOutcome(
                            item.Claim.ClaimId,
                            item.Claim.FindingId,
                            VerificationOutcome.SupportedKind,
                            FinalGateDecision.PublishDisposition,
                            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                            [],
                            VerificationOutcome.ModerateEvidence,
                            "Confirmed.",
                            VerificationOutcome.AiMicroVerifierEvaluator,
                            false))
                        .ToList());
            });

        var sut = new LocalReviewVerificationExecutor(extractor, verifier, CreateProtocolRecorder());
        var fileResult = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        var baselineFinding = CreateMergedFinding(
            "finding-baseline",
            "Baseline issue remains publishable.",
            FindingProvenanceKind.BaselineOnly);
        var prorvFinding = CreateMergedFinding(
            "finding-prorv",
            "ProRV-only issue needs stronger support.",
            FindingProvenanceKind.ProRVOnly);

        await sut.ApplyDetailedAsync(
            new ReviewResult(
                "summary",
                [
                    new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, baselineFinding.Message),
                    new ReviewComment("src/Foo.cs", 20, CommentSeverity.Warning, prorvFinding.Message),
                ]),
            fileResult,
            null,
            [],
            [baselineFinding, prorvFinding],
            null,
            CancellationToken.None);

        Assert.NotNull(capturedWorkItems);
        Assert.Collection(
            capturedWorkItems!,
            workItem => Assert.False(workItem.FindingProvenance.RequiresExplicitSupport),
            workItem => Assert.True(workItem.FindingProvenance.RequiresExplicitSupport));
    }

    private static CandidateReviewFinding CreateMergedFinding(
        string findingId,
        string message,
        FindingProvenanceKind findingProvenanceKind)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "late_steering_merge",
                "src/Foo.cs",
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: findingProvenanceKind),
            CommentSeverity.Warning,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            findingId == "finding-baseline" ? 10 : 20);
    }

    [Fact]
    public async Task ApplyAsync_FindingRequiringEvidenceContradictedByInvariant_IsDroppedWithoutJudge()
    {
        // The claim needs evidence because every finding does, but a known invariant fact already refutes
        // it. The deterministic contradiction decides, and the judge, which would confirm, is never asked.
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"confirmed\"}")));
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("var comment = new ReviewComment(path, line, severity, message);");
        var sut = new LocalReviewVerificationExecutor(
            new DeterministicReviewClaimExtractor(),
            new CompositeReviewFindingVerifier(new DeterministicLocalReviewVerifier(), new EvidenceBackedReviewVerifier()),
            CreateProtocolRecorder());
        var result = new ReviewResult(
            "summary",
            [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "ReviewComment.Message may be null when the model omits a message.")]);
        InvariantFact[] facts =
        [
            new(
                DomainReviewInvariantFactProvider.ReviewCommentMessageRequiredInvariantId,
                InvariantFact.DomainFamily,
                "ReviewComment.Message required",
                "ReviewComment constructor semantics",
                "message_non_null_and_non_empty",
                "ReviewComment requires a non-null, non-empty message value."),
        ];

        var actual = await sut.ApplyAsync(
            result,
            new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"),
            Guid.NewGuid(),
            facts,
            new ReviewVerificationContext(tools, "feature/x", judge, "judge-model"),
            CancellationToken.None);

        Assert.Empty(actual.Comments);
        Assert.Contains("dropped by deterministic verification", actual.Summary, StringComparison.Ordinal);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_FindingRequiringEvidence_IsWithheldWhenClaimExtractionFailsOrYieldsNothing(bool extractionThrows)
    {
        // Every finding must be confirmed with evidence. Without a claim nothing can confirm it, so a failed
        // or empty extraction withholds it, and the verifier is never asked.
        var extractor = Substitute.For<IReviewClaimExtractor>();
        if (extractionThrows)
        {
            extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(_ => throw new InvalidOperationException("extraction failed"));
        }
        else
        {
            extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns([]);
        }

        var verifier = Substitute.For<IReviewFindingVerifier>();
        var recorder = CreateProtocolRecorder();
        var sut = new LocalReviewVerificationExecutor(extractor, verifier, recorder);
        var result = new ReviewResult("summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Lookup may dereference null.")]);

        var actual = await sut.ApplyAsync(result, new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), Guid.NewGuid(), [], null, CancellationToken.None);

        Assert.Empty(actual.Comments);
        Assert.Contains("withheld pending stronger evidence", actual.Summary, StringComparison.Ordinal);
        _ = verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!);
        await recorder.Received(1).RecordVerificationEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.VerificationLocalDecision,
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains(FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_JudgesEveryFindingByItsTruth_NotByItsWording()
    {
        // The hedged comment describes a real defect and the firm comment describes one that is not there. The judge
        // reads the code and decides each finding, so the hedged finding is published and the firm one is withheld.
        const string hedgedTrue = "This might throw when the key is missing from the map, perhaps worth a look.";
        const string firmFalse = "Lookup always returns null, so every caller fails.";
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join("\n", callInfo.Arg<IEnumerable<ChatMessage>>().Select(message => message.Text));
                return prompt.Contains(hedgedTrue, StringComparison.Ordinal)
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"line 3 indexes the map without a check\"}"))
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"not_confirmed\",\"reason\":\"line 3 returns the mapped value\"}"));
            });
        var recorder = CreateProtocolRecorder();
        var details = new List<string>();
        await recorder.RecordVerificationEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.VerificationLocalDecision,
            Arg.Do<string?>(value => details.Add(value!)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        var sut = CreateJudgedExecutor(recorder);
        var result = new ReviewResult(
            "summary",
            [
                new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, hedgedTrue),
                new ReviewComment("src/Lookup.cs", 7, CommentSeverity.Error, firmFalse),
            ]);

        var actual = await sut.ApplyAsync(
            result, new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"), Guid.NewGuid(), [], CreateJudgeContext(judge), CancellationToken.None);

        var published = Assert.Single(actual.Comments);
        Assert.Equal(hedgedTrue, published.Message);
        Assert.Contains("1 candidate finding was withheld", actual.Summary, StringComparison.Ordinal);
        Assert.Equal(2, details.Count);
        var decisions = details.Select(value => JsonDocument.Parse(value).RootElement).ToList();
        var confirmed = Assert.Single(decisions, decision => decision.GetProperty("lineNumber").GetInt32() == 3);
        Assert.Equal("src/Lookup.cs", confirmed.GetProperty("filePath").GetString());
        Assert.Equal(EvidenceJudgeVerdicts.Confirmed, confirmed.GetProperty("judgeVerdict").GetString());
        Assert.Equal(FinalGateDecision.PublishDisposition, confirmed.GetProperty("disposition").GetString());
        Assert.Contains("indexes the map without a check", confirmed.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var refused = Assert.Single(decisions, decision => decision.GetProperty("lineNumber").GetInt32() == 7);
        Assert.Equal(EvidenceJudgeVerdicts.NotConfirmed, refused.GetProperty("judgeVerdict").GetString());
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, refused.GetProperty("disposition").GetString());
        Assert.Contains("returns the mapped value", refused.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WhenAFindingIsWithheld_PublishesTheOtherCommentWithItsMetadata()
    {
        const string kept = "Lookup indexes the map without checking the key.";
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join("\n", callInfo.Arg<IEnumerable<ChatMessage>>().Select(message => message.Text));
                return prompt.Contains(kept, StringComparison.Ordinal)
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"line 3 indexes the map\"}"))
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"not_confirmed\",\"reason\":\"line 7 checks the value\"}"));
            });
        var keptComment = new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, kept)
        {
            SourceReadGrounding = ReviewCommentReadGrounding.Covered,
            OriginPassKind = "MultiPassUnion",
            OriginPassIndex = 2,
            OriginPassLens = "inventory",
            OriginModelId = "model-a",
            OriginLogicalModelName = "Low Budget",
            OriginSymbolName = "Lookup",
            OriginSymbolKind = "method",
            ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
        };
        var result = new ReviewResult(
            "summary",
            [keptComment, new ReviewComment("src/Lookup.cs", 7, CommentSeverity.Error, "Lookup always returns null.")]);

        var actual = await CreateJudgedExecutor(CreateProtocolRecorder()).ApplyAsync(
            result, new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"), Guid.NewGuid(), [], CreateJudgeContext(judge), CancellationToken.None);

        var published = Assert.Single(actual.Comments);
        Assert.Same(keptComment, published);
        Assert.Equal(ReviewCommentReadGrounding.Covered, published.SourceReadGrounding);
        Assert.Equal("inventory", published.OriginPassLens);
        Assert.Equal("Lookup", published.OriginSymbolName);
    }

    [Fact]
    public async Task ApplyAsync_JudgesAndKeepsEveryConfirmedFinding_WithoutACountLimit()
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"present in the code\"}")));
        var sut = CreateJudgedExecutor(CreateProtocolRecorder());
        var comments = Enumerable.Range(1, 12)
            .Select(index => new ReviewComment("src/Lookup.cs", index * 5, CommentSeverity.Info, $"Minor style remark number {index}."))
            .ToList();

        var actual = await sut.ApplyAsync(
            new ReviewResult("summary", comments),
            new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"),
            Guid.NewGuid(),
            [],
            CreateJudgeContext(judge),
            CancellationToken.None);

        Assert.Equal(12, actual.Comments.Count);
        await judge.ReceivedWithAnyArgs(12).GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheJudgeFailsForEveryFinding_WithholdsThemAndRecordsTheOutage()
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new HttpRequestException("429 Too Many Requests"));
        var recorder = CreateProtocolRecorder();
        var degradedDetails = CaptureDegradedEvents(recorder);
        var sut = CreateJudgedExecutor(recorder);
        var result = new ReviewResult(
            "summary",
            [
                new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, "Lookup throws when the key is missing."),
                new ReviewComment("src/Lookup.cs", 7, CommentSeverity.Warning, "Lookup returns a stale value."),
            ]);

        var actual = await sut.ApplyAsync(
            result, new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"), Guid.NewGuid(), [], CreateJudgeContext(judge), CancellationToken.None);

        Assert.Empty(actual.Comments);
        Assert.Contains("Verification could not run for this file", actual.Summary, StringComparison.Ordinal);
        var degraded = JsonDocument.Parse(Assert.Single(degradedDetails)).RootElement;
        Assert.Equal("src/Lookup.cs", degraded.GetProperty("filePath").GetString());
        Assert.Equal("evidence_judge", degraded.GetProperty("degradedComponent").GetString());
        Assert.Equal(2, degraded.GetProperty("claimCount").GetInt32());
        Assert.Equal(2, degraded.GetProperty("degradedCount").GetInt32());
        Assert.True(degraded.GetProperty("allClaimsDegraded").GetBoolean());
        Assert.Equal(2, degraded.GetProperty("reasons").GetProperty(EvidenceJudgeDegradations.ProviderError).GetInt32());
    }

    [Fact]
    public async Task ApplyAsync_WhenTheJudgeFailsForSomeFindings_RecordsTheCountsAndKeepsTheSummaryNote()
    {
        const string confirmedMessage = "Lookup throws when the key is missing.";
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join("\n", callInfo.Arg<IEnumerable<ChatMessage>>().Select(message => message.Text));
                return prompt.Contains(confirmedMessage, StringComparison.Ordinal)
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"line 3\"}"))
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "I cannot decide this."));
            });
        var recorder = CreateProtocolRecorder();
        var degradedDetails = CaptureDegradedEvents(recorder);
        var sut = CreateJudgedExecutor(recorder);
        var result = new ReviewResult(
            "summary",
            [
                new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, confirmedMessage),
                new ReviewComment("src/Lookup.cs", 7, CommentSeverity.Warning, "Lookup returns a stale value."),
            ]);

        var actual = await sut.ApplyAsync(
            result, new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"), Guid.NewGuid(), [], CreateJudgeContext(judge), CancellationToken.None);

        Assert.Equal(confirmedMessage, Assert.Single(actual.Comments).Message);
        Assert.DoesNotContain("Verification could not run", actual.Summary, StringComparison.Ordinal);
        var degraded = JsonDocument.Parse(Assert.Single(degradedDetails)).RootElement;
        Assert.Equal(2, degraded.GetProperty("claimCount").GetInt32());
        Assert.Equal(1, degraded.GetProperty("degradedCount").GetInt32());
        Assert.False(degraded.GetProperty("allClaimsDegraded").GetBoolean());
        Assert.Equal(1, degraded.GetProperty("reasons").GetProperty(EvidenceJudgeDegradations.Unparseable).GetInt32());
    }

    [Fact]
    public async Task ApplyAsync_WithoutReviewTools_WithholdsEveryFindingAndRecordsTheJudgeAsUnavailable()
    {
        var recorder = CreateProtocolRecorder();
        var degradedDetails = CaptureDegradedEvents(recorder);
        var sut = CreateJudgedExecutor(recorder);
        var result = new ReviewResult("summary", [new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, "Lookup throws when the key is missing.")]);

        var actual = await sut.ApplyAsync(
            result,
            new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"),
            Guid.NewGuid(),
            [],
            new ReviewVerificationContext(null, "feature/x", Substitute.For<IChatClient>(), "judge-model"),
            CancellationToken.None);

        Assert.Empty(actual.Comments);
        Assert.Contains("Verification could not run for this file", actual.Summary, StringComparison.Ordinal);
        var degraded = JsonDocument.Parse(Assert.Single(degradedDetails)).RootElement;
        Assert.Equal(1, degraded.GetProperty("reasons").GetProperty(EvidenceJudgeDegradations.Unavailable).GetInt32());
    }

    [Fact]
    public async Task ApplyAsync_StoresAJudgeReasonOfAtMostTwoHundredEightyCharacters()
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{{\"verdict\":\"not_confirmed\",\"reason\":\"{new string('x', 900)}\"}}")));
        var recorder = CreateProtocolRecorder();
        var details = new List<string>();
        await recorder.RecordVerificationEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.VerificationLocalDecision,
            Arg.Do<string?>(value => details.Add(value!)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        var sut = CreateJudgedExecutor(recorder);

        await sut.ApplyAsync(
            new ReviewResult("summary", [new ReviewComment("src/Lookup.cs", 3, CommentSeverity.Warning, "Lookup throws when the key is missing.")]),
            new ReviewFileResult(Guid.NewGuid(), "src/Lookup.cs"),
            Guid.NewGuid(),
            [],
            CreateJudgeContext(judge),
            CancellationToken.None);

        var reason = JsonDocument.Parse(Assert.Single(details)).RootElement.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.True(reason!.Length <= 280);
    }

    private static List<string> CaptureDegradedEvents(IProtocolRecorder recorder)
    {
        var details = new List<string>();
        recorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationDegraded,
                Arg.Do<string?>(value => details.Add(value!)),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return details;
    }

    private static LocalReviewVerificationExecutor CreateJudgedExecutor(IProtocolRecorder recorder)
    {
        return new LocalReviewVerificationExecutor(
            new DeterministicReviewClaimExtractor(),
            new CompositeReviewFindingVerifier(new DeterministicLocalReviewVerifier(), new EvidenceBackedReviewVerifier(recorder)),
            recorder);
    }

    private static ReviewVerificationContext CreateJudgeContext(IChatClient judge)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("public string Lookup(string key)\n{\n    return _map[key];\n}");
        return new ReviewVerificationContext(tools, "feature/x", judge, "judge-model");
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
        return recorder;
    }
}
