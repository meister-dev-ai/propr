// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class LocalReviewVerificationExecutorTests
{
    [Fact]
    public async Task ApplyAsync_WhenNoClaimsAreExtracted_LeavesResultUntouchedAndNormalizesLineNumbers()
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

        Assert.Same(result, actual);
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
    public async Task ApplyAsync_WhenClaimExtractionThrows_RecordsDegradedEventAndLeavesResultUntouched()
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

        Assert.Same(result, actual);
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
                        .Select(item => VerificationOutcome.Supported(item.Claim, ReviewFindingGateReasonCodes.DefaultPublish, "Supported."))
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
                reviewPassKind: findingProvenanceKind == FindingProvenanceKind.ProRVOnly ? ReviewPassKind.ProRVAugmentation : ReviewPassKind.Baseline,
                findingProvenanceKind: findingProvenanceKind),
            CommentSeverity.Warning,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            findingId == "finding-baseline" ? 10 : 20);
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
