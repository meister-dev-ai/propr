// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    // A configured verification effort reaches the judge and the independent check on the Responses API. Chat
    // Completions receives no low, medium or high effort, as for a review pass.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheVerificationEffort_ReachesTheJudgeAndTheIndependentCheck(bool configured)
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(
                new AiReviewOptions { VerificationReasoningEffort = configured ? ReviewReasoningEffort.Medium : ReviewReasoningEffort.None }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = new JudgeEffort(null, configured ? "medium" : null);
        Assert.Equal([expected, expected], efforts);
    }

    // Without a configured verification effort, the judge runs with the effort None where it has no low effort to ask
    // for, and the independent check runs with it always. Each call then sends none to a model that accepts it, on
    // Chat Completions and on the Responses API, and no effort to any other model, as a review pass does.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithTheEffortNone_TheJudgeAndTheCheck_SendNoneOnlyToAModelThatAcceptsIt(bool accepted)
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsReasoningEffortNone: accepted, supportsLowReasoningEffort: false);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = accepted ? new JudgeEffort(ReasoningEffort.None, "none") : new JudgeEffort(null, null);
        Assert.Equal([expected, expected], efforts);
    }

    // The judge asks a model with low effort support for low effort, which reaches the Responses API only. The
    // independent check on the same model has no such rule and runs with the effort None.
    [Fact]
    public async Task AJudgeModelWithLowEffortSupport_IsAskedForLow_AndTheCheckSendsNone()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsReasoningEffortNone: true, supportsLowReasoningEffort: true);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal([new JudgeEffort(null, "low"), new JudgeEffort(ReasoningEffort.None, "none")], efforts);
    }

    // A judge model called through the Responses API also receives its effort in the shared reasoning setting, which
    // reaches a provider add-in. The independent check on the same model runs with the effort None.
    [Fact]
    public async Task AJudgeModelOnTheResponsesApi_AlsoReceivesItsEffortInTheSharedSetting()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsReasoningEffortNone: true, supportsLowReasoningEffort: true, usesResponsesApi: true);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal([new JudgeEffort(ReasoningEffort.Low, "low"), new JudgeEffort(ReasoningEffort.None, "none")], efforts);
    }

    // A judge that falls back to the reviewer's own model states a configured effort in the shared setting when that
    // model is called through the Responses API.
    [Fact]
    public async Task AJudgeOnTheReviewerModel_OnTheResponsesApi_AlsoReceivesTheEffortInTheSharedSetting()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { VerificationReasoningEffort = ReviewReasoningEffort.Medium }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model", UsesResponsesApi: true)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = new JudgeEffort(ReasoningEffort.Medium, "medium");
        Assert.Equal([expected, expected], efforts);
    }

    // A judge with no resolved runtime runs on the reviewer's own model and follows the setting of that model.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJudgeOnTheReviewerModel_FollowsWhetherThatModelAcceptsTheEffortNone(bool accepted)
    {
        var (judge, efforts) = JudgeRecordingEfforts();

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model", SupportsReasoningEffortNone: accepted)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = accepted ? new JudgeEffort(ReasoningEffort.None, "none") : new JudgeEffort(null, null);
        Assert.Equal([expected, expected], efforts);
    }

    // Records for every judge call the effort that Chat Completions reads and the effort the Responses API receives.
    private static (IChatClient Judge, List<JudgeEffort> Efforts) JudgeRecordingEfforts()
    {
        var efforts = new List<JudgeEffort>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var options = call.Arg<ChatOptions?>();
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
                var responses = options?.RawRepresentationFactory?.Invoke(null!) as CreateResponseOptions;
                efforts.Add(new JudgeEffort(options?.Reasoning?.Effort, responses?.ReasoningOptions?.ReasoningEffortLevel?.ToString()));
#pragma warning restore OPENAI001
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, LookupConfirmation("high")));
            });
        return (judge, efforts);
    }

    private static IAiRuntimeResolver ResolverFor(
        IChatClient judge,
        bool acceptsReasoningEffortNone,
        bool supportsLowReasoningEffort,
        bool usesResponsesApi = false)
    {
        var runtime = RuntimeFor(judge, "gpt-6-luna");
        var model = runtime.Model with { SupportsReasoningEffortNone = acceptsReasoningEffortNone };
        runtime.Model.Returns(model);
        runtime.SupportsLowReasoningEffort.Returns(supportsLowReasoningEffort);
        runtime.Capabilities.Returns(new AgentReviewRuntimeCapabilities(false, false, false, usesResponsesApi));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    /// <summary>The effort of one judge call as each OpenAI transport receives it.</summary>
    /// <param name="ChatCompletions">The effort the Chat Completions adapter reads, or null for none.</param>
    /// <param name="Responses">The effort the Responses API receives, or null for none.</param>
    private readonly record struct JudgeEffort(ReasoningEffort? ChatCompletions, string? Responses);
}
