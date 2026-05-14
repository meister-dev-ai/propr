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

        var actual = await sut.ApplyAsync(result, new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), Guid.NewGuid(), [], CancellationToken.None);

        Assert.Same(result, actual);
        Assert.NotNull(capturedFinding);
        Assert.Null(capturedFinding!.LineNumber);
        _ = verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default);
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
        verifier.VerifyAsync(Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<CancellationToken>())
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

        var actual = await sut.ApplyAsync(original, fileResult, null, [], CancellationToken.None);

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

        var actual = await sut.ApplyAsync(result, new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs"), protocolId, [], CancellationToken.None);

        Assert.Same(result, actual);
        _ = verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default);
        await protocolRecorder.Received().RecordVerificationEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.VerificationDegraded),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == "claim extraction failed"),
            Arg.Any<CancellationToken>());
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
