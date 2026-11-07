// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public class ReviewReasoningRequestTests
{
    // Chat Completions reads the effort from the standard setting only. An ordinary review pass leaves a configured
    // Low, Medium or High out of it, because OpenAI refuses function tools there with any effort other than none from
    // GPT-5.4 on.
    [Theory]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void OrdinaryReviewReasoning_DoesNotChangeItsStandardTransportOptions(ReviewReasoningEffort effort)
    {
        var options = new ChatOptions().ApplyReasoning(false, effort, supportsReasoningEffortNone: true);

        Assert.Null(options.Reasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoReasoning_StatesTheEffortNoneForAModelThatAcceptsIt(bool resolved)
    {
        var options = new ChatOptions { Temperature = 0.2f };

        if (resolved)
        {
            options.ApplyResolvedReasoning(false, ReviewReasoningEffort.None, supportsReasoningEffortNone: true);
        }
        else
        {
            options.ApplyReasoning(false, ReviewReasoningEffort.None, supportsReasoningEffortNone: true);
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

    // A client that speaks its provider's own protocol reads the neutral request, in which no reasoning is the
    // provider's default and needs no separate word.
    [Fact]
    public void NoReasoning_ReachesANativeProtocolClientAsTheNeutralNone()
    {
        var options = new ChatOptions().ApplyReasoning(true, ReviewReasoningEffort.None, supportsReasoningEffortNone: true);

        var raw = options.RawRepresentationFactory!(new NativeProtocolClient());

        var request = Assert.IsType<MeisterDev.Ai.Providers.Contracts.ProviderReasoningRequest>(raw);
        Assert.Equal(MeisterDev.Ai.Providers.Enums.ProviderReasoningEffort.None, request.Effort);
        Assert.True(request.CaptureReasoning);
    }

    // A resolved decision, which relayed calls and the verification calls use, states a configured effort for the
    // Responses API and leaves it out of the standard setting that Chat Completions reads, as an ordinary pass does.
    [Fact]
    public void ResolvedReasoning_StatesLowEffortForTheResponsesApiOnly()
    {
        var options = new ChatOptions().ApplyResolvedReasoning(false, ReviewReasoningEffort.Low, supportsReasoningEffortNone: true);

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
    [InlineData(ReviewReasoningEffort.None)]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.High)]
    public void AResolvedDecision_ClearsPreviouslyStatedStandardEffort(ReviewReasoningEffort effort)
    {
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        options.ApplyResolvedReasoning(false, effort);

        Assert.Null(options.Reasoning);
    }

    [Fact]
    public void ApplyReasoning_CaptureOffAndEffortNone_LeavesRawRepresentationUnset()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: false, ReviewReasoningEffort.None);

        // Default-none path: byte-identical to sending no reasoning options at all.
        Assert.Same(options, result);
        Assert.Null(result.RawRepresentationFactory);
    }

    // OpenAI rejects the whole request with HTTP 400 when a reasoning model is sent a temperature, so a
    // configured effort has to clear it. Every review call carries a temperature, so this failed all of them.
    [Theory]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void ApplyReasoning_WithAnEffort_DropsTheSamplingTemperature(ReviewReasoningEffort effort)
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: false, effort);

        Assert.Null(result.Temperature);
    }

    // An ordinary sampling model is not asked to reason, so its configured temperature has to survive.
    [Fact]
    public void ApplyReasoning_WithNoEffort_KeepsTheSamplingTemperature()
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        Assert.Equal(0.2f, result.Temperature);
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndEffortNone_RequestsSummaryButNoEffort()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        Assert.NotNull(result.RawRepresentationFactory);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        // Effort stays unset when None, so behavior matches the pre-feature capture-on request exactly.
        Assert.Null(createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOffAndEffortSet_SetsEffortUnconditionallyWithoutSummary()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: false, ReviewReasoningEffort.High);

        Assert.NotNull(result.RawRepresentationFactory);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        // Effort is applied independently of the reasoning-summary capture flag.
        Assert.Equal(ResponseReasoningEffortLevel.High, createOptions.ReasoningOptions!.ReasoningEffortLevel);
        // No summary requested when capture is off.
        Assert.Null(createOptions.ReasoningOptions.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndEffortSet_SetsBothSummaryAndEffort()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.Medium);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        Assert.Equal(ResponseReasoningEffortLevel.Medium, createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void ApplyReasoning_MapsEachEffortLevelToTheProviderLevel(ReviewReasoningEffort effort)
    {
        var result = new ChatOptions().ApplyReasoning(captureReasoning: false, effort);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        var expected = effort switch
        {
            ReviewReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReviewReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReviewReasoningEffort.High => ResponseReasoningEffortLevel.High,
            _ => (ResponseReasoningEffortLevel?)null,
        };
        Assert.Equal(expected, createOptions.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_Enabled_ReturnsFreshRawInstancePerInvocation()
    {
        var options = new ChatOptions().ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        var first = options.RawRepresentationFactory!(null!);
        var second = options.RawRepresentationFactory!(null!);

        Assert.NotSame(first, second);
    }

    private sealed class NativeProtocolClient : MeisterDev.Ai.Providers.Contracts.INativeProtocolChatClient
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
