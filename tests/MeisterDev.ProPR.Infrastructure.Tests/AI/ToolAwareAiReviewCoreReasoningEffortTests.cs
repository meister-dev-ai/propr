// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     The reasoning effort a review pass puts on its model requests, and the trace entry that records it.
/// </summary>
public sealed class ToolAwareAiReviewCoreReasoningEffortTests
{
    [Fact]
    public async Task APassWithNoReasoning_SendsNoneToAModelThatAcceptsIt_AndRecordsIt()
    {
        var (recorder, protocolId, sent) = await ReviewAsync(ReviewReasoningEffort.None, supportsReasoningEffortNone: true);

        Assert.Equal(ReasoningEffort.None, sent?.Reasoning?.Effort);
        var (details, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal("none", details.GetProperty("configuredEffort").GetString());
        Assert.True(details.GetProperty("modelAcceptsEffortNone").GetBoolean());
        Assert.Equal("sent", output.GetProperty("reasoningParameter").GetString());
        Assert.Equal("none", output.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task APassWithNoReasoning_RecordsThatItSentNoReasoningParameter_ToAModelWithoutNone()
    {
        var (recorder, protocolId, sent) = await ReviewAsync(ReviewReasoningEffort.None, supportsReasoningEffortNone: false);

        Assert.Null(sent?.Reasoning);
        var (details, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal("none", details.GetProperty("configuredEffort").GetString());
        Assert.False(details.GetProperty("modelAcceptsEffortNone").GetBoolean());
        Assert.Equal("omitted", output.GetProperty("reasoningParameter").GetString());
        Assert.False(output.TryGetProperty("effort", out _));
    }

    // A configured Low, Medium or High is stated for the Responses API and left out of the standard setting that Chat
    // Completions reads. Whether it reaches the wire therefore depends on the transport, and the pass records no
    // reasoning event for it.
    [Theory]
    [InlineData(ReviewReasoningEffort.Low, "low")]
    [InlineData(ReviewReasoningEffort.Medium, "medium")]
    [InlineData(ReviewReasoningEffort.High, "high")]
    public async Task APassWithAnEffort_StatesItForTheResponsesApiOnly_AndRecordsNoReasoningEvent(
        ReviewReasoningEffort configured,
        string expectedLevel)
    {
        var (recorder, _, sent) = await ReviewAsync(configured, supportsReasoningEffortNone: true);

        Assert.NotNull(sent);
        Assert.Null(sent.Reasoning);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var responseOptions = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(sent.RawRepresentationFactory!(null!));
        Assert.Equal(new OpenAI.Responses.ResponseReasoningEffortLevel(expectedLevel), responseOptions.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
        Assert.DoesNotContain(
            recorder.ReceivedCalls(),
            received => received.GetMethodInfo().Name == nameof(IProtocolRecorder.RecordReviewStrategyEventAsync)
                        && Equals(received.GetArguments()[1], ReviewProtocolEventNames.ReasoningEffortSent));
    }

    private static async Task<(IProtocolRecorder Recorder, Guid ProtocolId, ChatOptions? Sent)> ReviewAsync(
        ReviewReasoningEffort effort,
        bool supportsReasoningEffortNone)
    {
        ChatOptions? sent = null;
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(options => sent ??= options),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"summary":"Done.","comments":[],"loop_complete":true}""")));

        var recorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();
        var context = new ReviewSystemContext(null, [], null)
        {
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
            ActiveReasoningEffort = effort,
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(
                false,
                false,
                false,
                false,
                SupportsReasoningEffortNone: supportsReasoningEffortNone),
        };

        var core = new ToolAwareAiReviewCore(
            client,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxIterations = 1 }),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await core.ReviewAsync(
            new PullRequest(
                "https://dev.azure.com/org",
                "proj",
                "repo",
                "repo",
                1,
                1,
                "Test PR",
                null,
                "feature/x",
                "main",
                new List<ChangedFile>().AsReadOnly()),
            context);

        return (recorder, protocolId, sent);
    }

    private static (JsonElement Details, JsonElement Output) RecordedReasoningEvent(
        IProtocolRecorder recorder,
        Guid protocolId)
    {
        var call = Assert.Single(
            recorder.ReceivedCalls(),
            received => received.GetMethodInfo().Name == nameof(IProtocolRecorder.RecordReviewStrategyEventAsync)
                        && Equals(received.GetArguments()[1], ReviewProtocolEventNames.ReasoningEffortSent));
        var arguments = call.GetArguments();

        Assert.Equal(protocolId, arguments[0]);
        Assert.Null(arguments[4]);
        return (
            JsonDocument.Parse(Assert.IsType<string>(arguments[2])).RootElement,
            JsonDocument.Parse(Assert.IsType<string>(arguments[3])).RootElement);
    }
}
