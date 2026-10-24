// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    [Fact]
    public async Task ConfirmationThatWouldPublishAtMedium_IsHeldBackWhenTheEscalationCheckFindsItNotActionable()
    {
        var lowEffort = ClientAnswering(LookupConfirmation("medium"));
        var highEffort = ClientAnswering("""{"verdict":"not_actionable","reason":"Every caller validates the key first."}""");

        var outcome = await VerifyOnEffortTiersAsync(CommentSeverity.Warning, FindingConfidence.High, lowEffort, highEffort);

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal("withheld", outcome.ContradictionCheckStatus);
        Assert.Equal("ReviewHighEffort", outcome.ContradictionCheckRuntime!.Purpose);
        await lowEffort.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await highEffort.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CommentSeverity.Warning, "high", FindingConfidence.High, false)]
    [InlineData(CommentSeverity.Warning, "high", null, false)]
    [InlineData(CommentSeverity.Warning, "high", FindingConfidence.Medium, true)]
    [InlineData(CommentSeverity.Error, "medium", null, true)]
    [InlineData(CommentSeverity.Suggestion, "medium", FindingConfidence.High, false)]
    public async Task OnlyAnErrorOrWarningThatWouldPublishAtMediumOrBelow_IsEscalated(
        CommentSeverity severity, string level, FindingConfidence? proposed, bool escalated)
    {
        var lowEffort = ClientAnswering(LookupConfirmation(level));
        var highEffort = ClientAnswering(LookupConfirmation(level));

        var outcome = await VerifyOnEffortTiersAsync(severity, proposed, lowEffort, highEffort);

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        await highEffort.Received(escalated ? 1 : 0).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    private static IChatClient ClientAnswering(string answer)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        return client;
    }

    // The judge and the independent check resolve the low-effort purpose; only the escalation check resolves the
    // high-effort purpose.
    private static async Task<VerificationOutcome> VerifyOnEffortTiersAsync(
        CommentSeverity severity, FindingConfidence? proposed, IChatClient lowEffort, IChatClient highEffort)
    {
        var lowEffortRuntime = RuntimeFor(lowEffort, "low-effort-model");
        var highEffortRuntime = RuntimeFor(highEffort, "high-effort-model");
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewLowEffort, Arg.Any<CancellationToken>()).Returns(lowEffortRuntime);
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewHighEffort, Arg.Any<CancellationToken>()).Returns(highEffortRuntime);

        return Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: severity) with { ProposedConfidence = proposed }], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));
    }
}
