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
///     The reasoning level a review pass puts on its model requests, and the trace entry that records it.
/// </summary>
public sealed class ToolAwareAiReviewCoreReasoningEffortTests
{
    [Fact]
    public async Task APassAtTheLevelNone_SendsNoneToAModelThatAcceptsIt_AndRecordsIt()
    {
        var (recorder, protocolId, sent) = await ReviewAsync("none", ["none", "low", "high"]);

        Assert.Equal(ReasoningEffort.None, sent?.Reasoning?.Effort);
        var (details, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal("none", details.GetProperty("requestedLevel").GetString());
        Assert.Equal("sent", output.GetProperty("reasoningParameter").GetString());
        Assert.Equal("none", output.GetProperty("level").GetString());
        Assert.False(output.GetProperty("substituted").GetBoolean());
    }

    // The model's list has no level at or below none, so the request names no level, and the event records why.
    [Fact]
    public async Task APassAtTheLevelNone_RecordsThatItSentNoLevel_ToAModelWithoutNone()
    {
        var (recorder, protocolId, sent) = await ReviewAsync("none", ["low", "high"]);

        Assert.Null(sent?.Reasoning);
        var (_, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal("omitted", output.GetProperty("reasoningParameter").GetString());
        Assert.True(output.GetProperty("substituted").GetBoolean());
    }

    // A stored level the model no longer lists reaches it as the nearest lower level it accepts, and the event records
    // the substitution.
    [Fact]
    public async Task APassAtALevelTheModelDoesNotList_SendsTheNearestLowerLevel_AndRecordsTheSubstitution()
    {
        var (recorder, protocolId, sent) = await ReviewAsync("max", ["low", "high"]);

        var (details, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal("max", details.GetProperty("requestedLevel").GetString());
        Assert.Equal("high", output.GetProperty("level").GetString());
        Assert.True(output.GetProperty("substituted").GetBoolean());
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var responseOptions = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(sent!.RawRepresentationFactory!(null!));
        Assert.Equal(OpenAI.Responses.ResponseReasoningEffortLevel.High, responseOptions.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public async Task APassWithoutALevel_RecordsThatItSentNone()
    {
        var (recorder, protocolId, sent) = await ReviewAsync(null, ["none", "low"]);

        Assert.Null(sent?.Reasoning);
        var (details, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal(JsonValueKind.Null, details.GetProperty("requestedLevel").ValueKind);
        Assert.Equal("omitted", output.GetProperty("reasoningParameter").GetString());
        Assert.False(output.GetProperty("substituted").GetBoolean());
    }

    // A level other than none is stated for the Responses API and left out of the shared setting that Chat Completions
    // reads. Whether it reaches the wire therefore depends on the transport; the event records the level the pass
    // requests.
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task APassWithALevel_StatesItForTheResponsesApiOnly_AndRecordsIt(string level)
    {
        var (recorder, protocolId, sent) = await ReviewAsync(level, ["none", "low", "medium", "high"]);

        Assert.NotNull(sent);
        Assert.Null(sent.Reasoning);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var responseOptions = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(sent.RawRepresentationFactory!(null!));
        Assert.Equal(new OpenAI.Responses.ResponseReasoningEffortLevel(level), responseOptions.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
        var (_, output) = RecordedReasoningEvent(recorder, protocolId);
        Assert.Equal(level, output.GetProperty("level").GetString());
    }

    private static async Task<(IProtocolRecorder Recorder, Guid ProtocolId, ChatOptions? Sent)> ReviewAsync(
        string? level,
        IReadOnlyList<string>? modelLevels)
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
            ActiveReasoningLevel = level,
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(
                false,
                false,
                false,
                false,
                ReasoningLevels: modelLevels),
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
