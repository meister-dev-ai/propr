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
    // A configured verification level reaches the judge and the independent check on the Responses API. Chat
    // Completions receives no level other than none, as for a review pass.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheVerificationLevel_ReachesTheJudgeAndTheIndependentCheck(bool configured)
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { VerificationReasoningLevel = configured ? "medium" : null }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = new JudgeEffort(null, configured ? "medium" : null);
        Assert.Equal([expected, expected], efforts);
    }

    // Without a configured verification level and without low to ask for, the judge runs at the default level of its
    // model, and the independent check asks for the level none from a model whose levels hold it. A model that accepts
    // none and has it as its default level receives none from both calls, on Chat Completions and on the Responses API,
    // and any other model receives no level, as in a review pass.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAConfiguredLevel_TheJudgeAndTheCheck_SendNoneOnlyToAModelThatAcceptsIt(bool accepted)
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsNone: accepted, supportsLowReasoningEffort: false);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = accepted ? new JudgeEffort(ReasoningEffort.None, "none") : new JudgeEffort(null, null);
        Assert.Equal([expected, expected], efforts);
    }

    // The judge asks a model with low effort support for low, which reaches the Responses API only. The independent
    // check on the same model has no such rule and asks for the level none, which the model's levels hold.
    [Fact]
    public async Task AJudgeModelWithLowEffortSupport_IsAskedForLow_AndTheCheckSendsNone()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsNone: true, supportsLowReasoningEffort: true);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal([new JudgeEffort(null, "low"), new JudgeEffort(ReasoningEffort.None, "none")], efforts);
    }

    // The judge takes the level of its purpose assignment first, then the configured verification level, then low where
    // the model and its transport take it, then the level of its logical model, and the default level of its model
    // last. The independent check takes the configured verification level, and otherwise the level none from a model
    // whose levels hold it.
    [Theory]
    [InlineData("high", "medium", true, "none", "high", "medium")]
    [InlineData(null, "medium", true, "high", "medium", "medium")]
    [InlineData(null, null, true, "high", "low", "none")]
    [InlineData(null, null, false, "high", "high", "none")]
    public async Task TheJudgeAndTheCheck_TakeTheirLevelsInTheirOrder(
        string? purposeLevel,
        string? verificationLevel,
        bool supportsLow,
        string logicalModelLevel,
        string expectedJudgeLevel,
        string expectedCheckLevel)
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(
            judge, acceptsNone: true, supportsLowReasoningEffort: supportsLow, purposeLevel: purposeLevel, logicalModelLevel: logicalModelLevel);
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { VerificationReasoningLevel = verificationLevel }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal([expectedJudgeLevel, expectedCheckLevel], efforts.Select(effort => effort.Responses).ToList());
    }

    // A model whose levels no source knows and whose default level is none accepts none, which is what a runner learns
    // from an older control plane that sends only the flag, so the check still asks it for no reasoning.
    [Fact]
    public async Task TheCheck_AsksForNone_FromAModelWithoutAListWhoseDefaultLevelIsNone()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsNone: true, supportsLowReasoningEffort: false);
        var runtime = await resolver.ResolveChatRuntimeAsync(Guid.Empty, AiPurpose.ReviewVerification);
        runtime.Capabilities.Returns(new AgentReviewRuntimeCapabilities(false, false, false, false, ReasoningLevels: null, DefaultReasoningLevel: "none"));

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal("none", efforts[^1].Responses);
    }

    // A judge model called through the Responses API also receives its level in the shared reasoning setting, which
    // reaches a provider add-in. The independent check on the same model asks for the level none.
    [Fact]
    public async Task AJudgeModelOnTheResponsesApi_AlsoReceivesItsEffortInTheSharedSetting()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var resolver = ResolverFor(judge, acceptsNone: true, supportsLowReasoningEffort: true, usesResponsesApi: true);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", Substitute.For<IChatClient>(), "reviewer-model", Guid.NewGuid(), resolver)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal([new JudgeEffort(ReasoningEffort.Low, "low"), new JudgeEffort(ReasoningEffort.None, "none")], efforts);
    }

    // A judge that falls back to the reviewer's own model states a configured level in the shared setting when that
    // model is called through the Responses API.
    [Fact]
    public async Task AJudgeOnTheReviewerModel_OnTheResponsesApi_AlsoReceivesTheEffortInTheSharedSetting()
    {
        var (judge, efforts) = JudgeRecordingEfforts();
        var verifier = new EvidenceBackedReviewVerifier(
            reviewOptions: Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { VerificationReasoningLevel = "medium" }));

        var outcome = Assert.Single(
            await verifier.VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(AllocationTools(LookupSource), "source", judge, "judge-model", UsesResponsesApi: true)));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var expected = new JudgeEffort(ReasoningEffort.Medium, "medium");
        Assert.Equal([expected, expected], efforts);
    }

    // A judge with no resolved runtime runs on the reviewer's own model and follows the levels and the default level of
    // that model.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJudgeOnTheReviewerModel_FollowsTheLevelsOfThatModel(bool accepted)
    {
        var (judge, efforts) = JudgeRecordingEfforts();

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(LookupPath, 1, severity: CommentSeverity.Warning)], [],
                new ReviewVerificationContext(
                    AllocationTools(LookupSource), "source", judge, "judge-model",
                    ReasoningLevels: LevelsOf(accepted), DefaultReasoningLevel: accepted ? "none" : null)));

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

    // A judging model that accepts the level none lists it among its levels and has it as its default level.
    private static IAiRuntimeResolver ResolverFor(
        IChatClient judge,
        bool acceptsNone,
        bool supportsLowReasoningEffort,
        bool usesResponsesApi = false,
        string? purposeLevel = null,
        string? logicalModelLevel = null)
    {
        var runtime = RuntimeFor(judge, "gpt-6-luna");
        var model = runtime.Model with { ReasoningLevels = LevelsOf(acceptsNone), DefaultReasoningLevel = acceptsNone ? "none" : null };
        runtime.Model.Returns(model);
        runtime.SupportsLowReasoningEffort.Returns(supportsLowReasoningEffort);
        runtime.PurposeReasoningLevel.Returns(purposeLevel);
        runtime.LogicalModelReasoningLevel.Returns(logicalModelLevel);
        runtime.Capabilities.Returns(
            new AgentReviewRuntimeCapabilities(
                false, false, false, usesResponsesApi, ReasoningLevels: model.ReasoningLevels, DefaultReasoningLevel: model.DefaultReasoningLevel));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    // The levels of a model that accepts the level none, or of a reasoning model that refuses it.
    private static IReadOnlyList<string> LevelsOf(bool acceptsNone)
    {
        return acceptsNone ? ["none", "low", "medium", "high"] : ["low", "medium", "high"];
    }

    /// <summary>The effort of one judge call as each OpenAI transport receives it.</summary>
    /// <param name="ChatCompletions">The effort the Chat Completions adapter reads, or null for none.</param>
    /// <param name="Responses">The effort the Responses API receives, or null for none.</param>
    private readonly record struct JudgeEffort(ReasoningEffort? ChatCompletions, string? Responses);
}
