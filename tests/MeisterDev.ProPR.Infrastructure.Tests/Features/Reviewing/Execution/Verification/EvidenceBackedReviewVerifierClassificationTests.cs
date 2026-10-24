// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    [Theory]
    [InlineData("defect", "defect")]
    [InlineData("improvement", "improvement")]
    [InlineData("unsupported", null)]
    public async Task ExplicitKind_NormalizesValuesForConfirmedVerdicts(string kind, string? expected)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("old();");
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"The source establishes the claim.\",\"finding_kind\":\"" + kind + "\"}")));
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem("a.cs", 1)], [],
                new ReviewVerificationContext(tools, "source", judge, "judge")));
        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(expected, outcome.VerifiedFindingKind);
    }
}
