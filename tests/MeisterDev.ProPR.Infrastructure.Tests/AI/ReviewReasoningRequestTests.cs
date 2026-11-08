// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public class ReviewReasoningRequestTests
{
    private static readonly IReadOnlyList<string> AcceptsNoneToHigh = ["none", "low", "medium", "high"];

    // Chat Completions reads the effort from the shared setting only. An ordinary review pass on a model called through
    // Chat Completions leaves every level other than none out of it, because OpenAI refuses function tools there with
    // any effort other than none from GPT-5.4 on.
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void OrdinaryReviewReasoning_DoesNotChangeItsStandardTransportOptions(string level)
    {
        var options = new ChatOptions().ApplyReasoning(false, level, AcceptsNoneToHigh);

        Assert.Null(options.Reasoning);
    }

    // For a model called through the Responses API, a level is also stated in the shared setting, which a provider
    // add-in reads, as far as the setting has a value for it, and the Responses options carry the level by name for a
    // client that reads them. An ordinary pass and a resolved decision behave alike.
    [Theory]
    [InlineData("minimal", ReasoningEffort.Low, false)]
    [InlineData("low", ReasoningEffort.Low, false)]
    [InlineData("medium", ReasoningEffort.Medium, false)]
    [InlineData("high", ReasoningEffort.High, false)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh, true)]
    [InlineData("max", ReasoningEffort.ExtraHigh, true)]
    public void ALevel_IsStatedInTheSharedSettingAndByNameForTheResponsesApi(string level, ReasoningEffort expected, bool resolved)
    {
        var options = resolved
            ? new ChatOptions().ApplyResolvedReasoning(false, level, usesResponsesApi: true)
            : new ChatOptions().ApplyReasoning(false, level, usesResponsesApi: true);

        Assert.Equal(expected, options.Reasoning!.Effort);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal(new ResponseReasoningEffortLevel(level), raw.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    // The level none follows the rule of every transport: a model whose list holds it receives it, and a model whose
    // list does not receives no level.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLevelNone_OnTheResponsesApi_ReachesOnlyAModelThatAcceptsIt(bool acceptsNone)
    {
        IReadOnlyList<string> levels = acceptsNone ? ["none", "low"] : ["low", "high"];

        var options = new ChatOptions().ApplyResolvedReasoning(false, "none", levels, usesResponsesApi: true);

        ReasoningEffort? expected = acceptsNone ? ReasoningEffort.None : null;
        Assert.Equal(expected, options.Reasoning?.Effort);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));
        if (acceptsNone)
        {
            Assert.Equal(ResponseReasoningEffortLevel.None, raw.ReasoningOptions!.ReasoningEffortLevel);
        }
        else
        {
            Assert.Null(raw.ReasoningOptions);
        }
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLevelNone_StatesTheEffortNoneAndKeepsTheTemperature(bool resolved)
    {
        var options = new ChatOptions { Temperature = 0.2f };

        if (resolved)
        {
            options.ApplyResolvedReasoning(false, "none", AcceptsNoneToHigh);
        }
        else
        {
            options.ApplyReasoning(false, "none", AcceptsNoneToHigh);
        }

        Assert.Equal(ReasoningEffort.None, options.Reasoning!.Effort);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal(ResponseReasoningEffortLevel.None, raw.ReasoningOptions!.ReasoningEffortLevel);
        Assert.Null(raw.ReasoningOptions.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001

        // A model told not to reason samples like any other model, so the configured temperature stays.
        Assert.Equal(0.2f, options.Temperature);
    }

    // A client that speaks its provider's own protocol, which includes the provider add-ins, reads the neutral request
    // with the level by name and the nearest neutral value for a family that does not read the name.
    [Theory]
    [InlineData("none", ProviderReasoningEffort.None)]
    [InlineData("minimal", ProviderReasoningEffort.Low)]
    [InlineData("medium", ProviderReasoningEffort.Medium)]
    [InlineData("xhigh", ProviderReasoningEffort.High)]
    [InlineData("max", ProviderReasoningEffort.High)]
    public void ALevel_ReachesANativeProtocolClientByNameInTheNeutralRequest(string level, ProviderReasoningEffort expected)
    {
        var options = new ChatOptions().ApplyReasoning(true, level);

        var request = Assert.IsType<ProviderReasoningRequest>(options.RawRepresentationFactory!(new NativeProtocolClient()));

        Assert.Equal(level, request.Level);
        Assert.Equal(expected, request.Effort);
        Assert.True(request.CaptureReasoning);
    }

    // A stored level can fall out of the model's list; the model then receives the nearest lower level its list holds.
    [Fact]
    public void ALevelTheModelDoesNotAccept_ReachesItAsTheNearestLowerLevelItAccepts()
    {
        var options = new ChatOptions().ApplyReasoning(false, "max", ["low", "medium", "high"], usesResponsesApi: true);

        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
        var request = Assert.IsType<ProviderReasoningRequest>(options.RawRepresentationFactory!(new NativeProtocolClient()));
        Assert.Equal("high", request.Level);
    }

    [Fact]
    public void ALevelWithNoLowerLevelTheModelAccepts_LeavesTheRequestUnchanged()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(false, "low", ["medium", "high"]);

        Assert.Same(options, result);
        Assert.Null(result.RawRepresentationFactory);
    }

    // A model whose levels no source knows receives the level as configured, as before models carried their levels.
    [Fact]
    public void AModelWithoutALevelList_ReceivesTheLevelAsConfigured()
    {
        var options = new ChatOptions().ApplyReasoning(false, "max", acceptedLevels: null, usesResponsesApi: true);

        Assert.Equal(ReasoningEffort.ExtraHigh, options.Reasoning!.Effort);
        var request = Assert.IsType<ProviderReasoningRequest>(options.RawRepresentationFactory!(new NativeProtocolClient()));
        Assert.Equal("max", request.Level);
    }

    // A resolved decision, which relayed calls and the verification calls use, states a level for the Responses API and
    // leaves it out of the shared setting that Chat Completions reads, as an ordinary pass does.
    [Fact]
    public void ResolvedReasoning_StatesLowForTheResponsesApiOnly()
    {
        var options = new ChatOptions().ApplyResolvedReasoning(false, "low", AcceptsNoneToHigh);

        Assert.Null(options.Reasoning);
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal(ResponseReasoningEffortLevel.Low, raw.ReasoningOptions!.ReasoningEffortLevel);
        Assert.Null(raw.ReasoningOptions.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001
    }

    // A resolved decision replaces what the options carried, so an effort stated there earlier does not reach Chat
    // Completions.
    [Theory]
    [InlineData(null)]
    [InlineData("low")]
    [InlineData("high")]
    public void AResolvedDecision_ClearsPreviouslyStatedStandardEffort(string? level)
    {
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        options.ApplyResolvedReasoning(false, level);

        Assert.Null(options.Reasoning);
    }

    [Fact]
    public void ApplyReasoning_CaptureOffAndNoLevel_LeavesRawRepresentationUnset()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: false, null);

        // Byte-identical to sending no reasoning options at all.
        Assert.Same(options, result);
        Assert.Null(result.RawRepresentationFactory);
    }

    // OpenAI rejects the whole request with HTTP 400 when a reasoning model is sent a temperature, so a level other
    // than none has to clear it.
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    public void ApplyReasoning_WithALevel_DropsTheSamplingTemperature(string level)
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: false, level);

        Assert.Null(result.Temperature);
    }

    // An ordinary sampling model is not asked to reason, so its configured temperature has to survive.
    [Fact]
    public void ApplyReasoning_WithNoLevel_KeepsTheSamplingTemperature()
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: true, null);

        Assert.Equal(0.2f, result.Temperature);
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndNoLevel_RequestsSummaryButNoEffort()
    {
        var result = new ChatOptions().ApplyReasoning(captureReasoning: true, null);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        Assert.Null(createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOffAndALevel_SetsTheLevelWithoutSummary()
    {
        var result = new ChatOptions().ApplyReasoning(captureReasoning: false, "high");

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.Equal(ResponseReasoningEffortLevel.High, createOptions.ReasoningOptions!.ReasoningEffortLevel);
        Assert.Null(createOptions.ReasoningOptions.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndALevel_SetsBothSummaryAndLevel()
    {
        var result = new ChatOptions().ApplyReasoning(captureReasoning: true, "medium");

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        Assert.Equal(ResponseReasoningEffortLevel.Medium, createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    // The shaping records the level it sent, so a relay that rebuilds the request keeps the pass's level.
    [Fact]
    public void ShapedReasoningLevel_IsTheLevelTheRequestNames()
    {
        var options = new ChatOptions().ApplyReasoning(false, "xhigh", ["low", "high"]);

        Assert.True(options.CarriesAReasoningRequest());
        Assert.Equal("high", options.ShapedReasoningLevel());
    }

    [Fact]
    public void ApplyReasoning_Enabled_ReturnsFreshRawInstancePerInvocation()
    {
        var options = new ChatOptions().ApplyReasoning(captureReasoning: true, null);

        var first = options.RawRepresentationFactory!(null!);
        var second = options.RawRepresentationFactory!(null!);

        Assert.NotSame(first, second);
    }

    private sealed class NativeProtocolClient : INativeProtocolChatClient
    {
        public string NativeProtocol => "test/native:Messages";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
