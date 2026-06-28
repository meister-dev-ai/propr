// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class EvidenceBackedReviewVerifierTests
{
    [Fact]
    public async Task ReadsWindowCenteredOnAnchorLine_AndPromotesOnConfirm()
    {
        const string path = "src/Big.cs";
        const int anchorLine = 350;

        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(path, "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("public string Lookup(string? key)\n{\n    return _map[key].ToString();\n}");

        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"verdict\":\"confirmed\",\"reason\":\"dereferences a possibly-null map lookup\"}")));

        var sut = new EvidenceBackedReviewVerifier();

        var outcomes = await sut.VerifyAsync(
            [CreateWorkItem(path, anchorLine)],
            [],
            new ReviewVerificationContext(tools, "source", judge, "judge-model"),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.SupportedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);

        // The anchor window is centered on the claim's line (anchor − half-window), not read from the file
        // head, so a defect deep in a large file still reaches the judge.
        await tools.Received(1).GetFileContentAsync(path, "source", 150, 549, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadsFromFileHead_WhenAnchorLineUnknown()
    {
        const string path = "src/Big.cs";

        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(path, "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("some source");

        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"verdict\":\"not_confirmed\",\"reason\":\"no evidence\"}")));

        var sut = new EvidenceBackedReviewVerifier();

        await sut.VerifyAsync(
            [CreateWorkItem(path, null)],
            [],
            new ReviewVerificationContext(tools, "source", judge, "judge-model"),
            CancellationToken.None);

        // Unknown anchor line preserves the original file-head read.
        await tools.Received(1).GetFileContentAsync(path, "source", 1, 400, Arg.Any<CancellationToken>());
    }

    private static VerificationWorkItem CreateWorkItem(string anchorFilePath, int? anchorLineNumber)
    {
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "The method `lookup` dereferences a value that may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.ApiOrSymbolUsageFamily,
            subjectIdentifier: "lookup",
            anchorFilePath: anchorFilePath,
            anchorLineNumber: anchorLineNumber,
            requiresSymbolEvidence: true);

        return new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
            ClaimDescriptor.LocalStage,
            VerificationWorkItem.AnchorOnlyScope,
            false);
    }
}
