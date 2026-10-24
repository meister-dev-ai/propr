// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    private const string LookupPath = "src/Lookup.cs";
    private const string LookupSource = "return lookup(key);";

    [Fact]
    public async Task ConfirmationAfterTheLastLookup_IsCheckedIndependentlyAndCappedAtMedium()
    {
        var confirmed = LookupConfirmation("high");
        var responses = Enumerable.Range(1, EvidenceJudgeToolLoop.MaxToolCallsPerClaim)
            .Select(index => ToolCallResponse($"read-{index}", "get_file_content", """{"path":"src/Other.cs","branch":"source","startLine":1,"endLine":5}"""))
            .Append(new ChatResponse(new ChatMessage(ChatRole.Assistant, confirmed)))
            .Append(new ChatResponse(new ChatMessage(ChatRole.Assistant, confirmed)))
            .ToArray();
        var calls = 0;
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => responses[Math.Min(calls++, responses.Length - 1)]);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1)], [], new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeToolLoop.MaxToolCallsPerClaim + 2, calls);
        Assert.Contains(EvidenceBackedReviewVerifier.LookupBudgetAssumption, outcome.ConfidenceEvidence!.Assumptions);
        Assert.Equal("unresolved_assumptions", outcome.ContradictionCheckStatus);
        Assert.Equal(FindingConfidence.Medium, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    [Fact]
    public async Task ConfirmationBeforeTheLastLookup_KeepsItsLevelWithoutAnIndependentCheck()
    {
        var calls = 0;
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, LookupConfirmation("high")));
            });

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1)], [], new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(1, calls);
        Assert.Null(outcome.ContradictionCheckStatus);
        Assert.Empty(outcome.ConfidenceEvidence!.Assumptions);
        Assert.Equal(FindingConfidence.High, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    [Theory]
    [InlineData(CommentSeverity.Error, true)]
    [InlineData(CommentSeverity.Warning, true)]
    [InlineData(CommentSeverity.Suggestion, false)]
    [InlineData(CommentSeverity.Info, false)]
    public async Task ConfirmedFindingThatAsksForAChange_IsHeldBackWhenTheIndependentCheckFindsItNotActionable(
        CommentSeverity severity, bool checkedIndependently)
    {
        var answers = new Queue<string>([LookupConfirmation("high"), """{"verdict":"not_actionable","reason":"Every caller validates the key first."}"""]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answers.Dequeue())));

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: severity)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(checkedIndependently ? 0 : 1, answers.Count);
        Assert.Equal(checkedIndependently ? FinalGateDecision.SummaryOnlyDisposition : FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(checkedIndependently ? "withheld" : null, outcome.ContradictionCheckStatus);
    }

    [Fact]
    public async Task ClaimThatLeavesItsTriggerOpen_IsHeldBackWhenTheIndependentCheckFindsItNotActionable()
    {
        var answers = new Queue<string>([LookupConfirmation("high"), """{"verdict":"not_actionable","reason":"Every caller validates the key first."}"""]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answers.Dequeue())));
        var item = CreateWorkItem(
            LookupPath, 1, claimText: "The method `lookup` dereferences the key. Whether callers can pass a null key is not shown in this diff.");

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [item], [], new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Empty(answers);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeVerdicts.NotActionable, outcome.JudgeVerdict);
        Assert.Equal("withheld", outcome.ContradictionCheckStatus);
    }

    [Theory]
    [InlineData("""{"verdict":"insufficient_context","reason":"The caller lives in another service."}""", false, true)]
    [InlineData("""{"verdict":"insufficient_context","reason":"The caller lives in another service."}""", true, false)]
    [InlineData("""{"verdict":"not_actionable","reason":"No reachable caller passes a null key."}""", false, true)]
    [InlineData("""{"verdict":"intended","reason":"The comment above the call states that a missing key returns null."}""", false, true)]
    public async Task UnconfirmedIndependentCheck_HoldsTheFindingBackLikeTheFirstJudge(string check, bool passHadTools, bool held)
    {
        var answers = new Queue<string>([LookupConfirmation("certain"), check]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answers.Dequeue())));

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1) with { RequiresCertaintyCheck = true }], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model", PassReviewedWithRepositoryTools: passHadTools)));

        Assert.Equal(held ? FinalGateDecision.SummaryOnlyDisposition : FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(held ? "withheld" : "unresolved", outcome.ContradictionCheckStatus);
    }

    [Fact]
    public async Task IndependentCheck_ReadsTheRepositoryBeforeRefutingAClaim()
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(LookupPath, "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(LookupSource);
        tools.GetFileContentAsync("src/Caller.cs", "source", 1, 5, Arg.Any<CancellationToken>()).Returns("if (key is null) { return null; }");
        var responses = new Queue<ChatResponse>(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, LookupConfirmation("certain"))),
            ToolCallResponse("caller", "get_file_content", """{"path":"src/Caller.cs","branch":"source","startLine":1,"endLine":5}"""),
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    ConfidenceVerdict(
                        "not_confirmed", "high", "if (key is null) { return null; }", "The only caller returns before the call when the key is null.",
                        "A null key never reaches lookup.", "The caller guards the key before the call.", "src/Caller.cs"))),
        ]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => responses.Dequeue());
        var recorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier(recorder).VerifyAsync(
                [CreateWorkItem(LookupPath, 1) with { RequiresCertaintyCheck = true }], [],
                new ReviewVerificationContext(tools, "source", judge, "judge-model", ProtocolId: protocolId)));

        Assert.Equal(FinalGateDecision.DropDisposition, outcome.RecommendedDisposition);
        Assert.Equal("refuted", outcome.ContradictionCheckStatus);
        await tools.Received(1).GetFileContentAsync("src/Caller.cs", "source", 1, 5, Arg.Any<CancellationToken>());
        await recorder.Received(1).RecordVerificationEventAsync(
            protocolId,
            "evidence_judge_tool_call",
            Arg.Is<string?>(details => details != null && details.Contains("contradiction_check")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LowerLevelFromTheIndependentCheck_PublishesTheEvidenceThatExplainsIt()
    {
        var lowered = ConfidenceVerdict(
            "confirmed", "medium", LookupSource, "Only callers in the import path pass an unchecked key.",
            "An import with a missing key receives a null result.", "The request path validates the key before the call.", LookupPath);
        var answers = new Queue<string>([LookupConfirmation("certain"), lowered]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answers.Dequeue())));

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1) with { RequiresCertaintyCheck = true }], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));
        var resolved = ReviewFindingConfidence.Resolve(null, [outcome])!;

        Assert.Equal(FindingConfidence.Medium, resolved.Confidence);
        Assert.Equal("Only callers in the import path pass an unchecked key.", outcome.ConfidenceEvidence!.Witness);
        Assert.Contains("The request path validates the key before the call.", resolved.ConfidenceReason);
    }

    private static string LookupConfirmation(string level) => ConfidenceVerdict(
        "confirmed", level, LookupSource, "A missing key reaches lookup through this call.", "The caller receives a null result.",
        "No guard before the call excludes a missing key.", LookupPath);
}
