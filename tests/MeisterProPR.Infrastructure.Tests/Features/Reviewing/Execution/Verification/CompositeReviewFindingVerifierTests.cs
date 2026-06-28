// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class CompositeReviewFindingVerifierTests
{
    [Fact]
    public async Task WhenContextFlagDisabled_PreservesDeterministicWithhold()
    {
        var sut = CreateSut();

        var outcomes = await sut.VerifyAsync(
            [CreateWithheldWorkItem()],
            [],
            new ReviewVerificationContext(null, "source", null, null, EvidenceVerificationEnabled: false),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.NonVerifiableKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(VerificationOutcome.DeterministicRulesEvaluator, outcome.EvaluatedBy);
    }

    [Fact]
    public async Task WhenContextFlagEnabledButNoEvidenceChannel_PreservesDeterministicWithhold()
    {
        var sut = CreateSut();

        // Tools null → no way to gather evidence → the composite must not escalate, leaving the
        // conservative deterministic outcome untouched even when the per-client flag is on.
        var outcomes = await sut.VerifyAsync(
            [CreateWithheldWorkItem()],
            [],
            new ReviewVerificationContext(null, "source", null, null, EvidenceVerificationEnabled: true),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(VerificationOutcome.DeterministicRulesEvaluator, outcome.EvaluatedBy);
    }

    [Fact]
    public async Task WhenEnabledAndEvidenceConfirms_PromotesWithheldClaimToPublish()
    {
        const string anchorPath = "src/Service.cs";

        // Anchor source the evidence verifier reads; the judge then confirms the claim against it.
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(anchorPath, "source", 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("public string Lookup(string? key)\n{\n    return _map[key].ToString();\n}");

        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"verdict\":\"confirmed\",\"reason\":\"line 3 dereferences a possibly-null map lookup\"}")));

        var sut = CreateSut();

        var outcomes = await sut.VerifyAsync(
            [CreateWithheldWorkItem(anchorPath)],
            [],
            new ReviewVerificationContext(tools, "source", judge, "judge-model", EvidenceVerificationEnabled: true),
            CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerificationOutcome.SupportedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(VerificationOutcome.AiMicroVerifierEvaluator, outcome.EvaluatedBy);
    }

    private static CompositeReviewFindingVerifier CreateSut()
    {
        return new CompositeReviewFindingVerifier(
            new DeterministicLocalReviewVerifier(),
            new EvidenceBackedReviewVerifier());
    }

    private static VerificationWorkItem CreateWithheldWorkItem(string? anchorFilePath = null)
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
            subjectIdentifier: "lookup",
            anchorFilePath: anchorFilePath,
            anchorLineNumber: anchorFilePath is null ? null : 3,
            requiresSymbolEvidence: true);

        return new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
            ClaimDescriptor.LocalStage,
            VerificationWorkItem.AnchorOnlyScope,
            false);
    }
}
