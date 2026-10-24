// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheVerificationEffort_ReachesTheJudgeAndTheIndependentCheck(bool configured)
    {
        var efforts = new List<ReasoningEffort?>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                efforts.Add(call.Arg<ChatOptions?>()?.Reasoning?.Effort);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, LookupConfirmation("high")));
            });
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(
                new AiReviewOptions { VerificationReasoningEffort = configured ? ReviewReasoningEffort.Medium : ReviewReasoningEffort.None }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        ReasoningEffort? expected = configured ? ReasoningEffort.Medium : null;
        Assert.Equal([expected, expected], efforts);
    }
}
