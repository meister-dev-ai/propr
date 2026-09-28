// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Rebuilding a relayed completion's options on the control plane. What the runner's pipeline shaped has
///     to reach the provider intact: tools as declarations, the ceiling and temperature as sent, and the
///     reasoning settings re-applied against the actual client. Otherwise a remote review runs as a different
///     review from the same job run in process.
/// </summary>
public sealed class RunnerRelayedChatOptionsTests
{
    [Fact]
    public void NothingOnTheWire_MeansNoOptions()
    {
        Assert.Null(RunnerRelayedChatOptions.ToChatOptions(null));
    }

    [Fact]
    public void TemperatureAndCeiling_SurviveTheRebuild()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(0.2f, 9000));

        Assert.NotNull(options);
        Assert.Equal(0.2f, options!.Temperature);
        Assert.Equal(9000, options.MaxOutputTokens);
    }

    [Fact]
    public void AToolDeclaration_BecomesAFunctionTheProviderCanSerialize()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement;

        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(Tools: [new RunnerChatToolDefinition("get_file_content", "Reads a file at head.", schema)]));

        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
        Assert.Equal("get_file_content", tool.Name);
        Assert.Equal("Reads a file at head.", tool.Description);
        Assert.Equal(schema.GetRawText(), tool.JsonSchema.GetRawText());
    }

    // A parameterless tool still needs a schema the provider accepts; an empty object is the neutral one.
    [Fact]
    public void AToolDeclarationWithoutASchema_GetsAnEmptyObjectSchema()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(Tools: [new RunnerChatToolDefinition("list_changed_files")]));

        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
        Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
    }

    // The implementation lives on the runner; reaching invocation here means a composition bug, and it
    // must fail rather than return something a review would treat as a tool answer.
    [Fact]
    public async Task ARelayedDeclaration_RefusesInvocation()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(Tools: [new RunnerChatToolDefinition("get_file_content")]));

        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await tool.InvokeAsync(new AIFunctionArguments()));
    }

    [Fact]
    public void ReasoningKnobs_AreReappliedForANativeProtocolClient()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(Temperature: 0.4f, ReasoningEffort: "high", CaptureReasoning: true));

        // A model asked to reason takes no sampling temperature; the rebuild has to drop it exactly as the
        // in-process path does.
        Assert.Null(options!.Temperature);

        var raw = options.RawRepresentationFactory!(new FakeNativeClient());
        var request = Assert.IsType<ProviderReasoningRequest>(raw);
        Assert.Equal(ProviderReasoningEffort.High, request.Effort);
        Assert.True(request.CaptureReasoning);
    }

    // A newer runner naming a level this build does not know still gets its completion, at the provider's
    // default effort, and keeps its temperature, because no effort was applied.
    [Fact]
    public void AnUnknownEffort_FallsBackToTheProviderDefault()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(Temperature: 0.4f, ReasoningEffort: "galactic"));

        Assert.Equal(0.4f, options!.Temperature);
        Assert.Null(options.RawRepresentationFactory);
    }

    private sealed class FakeNativeClient : INativeProtocolChatClient
    {
        public string NativeProtocol => ProviderDeclaredProtocolModes.Auto;

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AnOutputCeilingOutsideTheAcceptedRangeIsDropped(int relayed)
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(MaxOutputTokens: relayed));

        Assert.Null(options!.MaxOutputTokens);
    }

    [Fact]
    public void AnOutputCeilingInsideTheAcceptedRangeIsHonoured()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(MaxOutputTokens: 8192));

        Assert.Equal(8192, options!.MaxOutputTokens);
    }

    // A runner states its own reasoning-summary opt-in, so an outdated or hostile one can ask for reasoning a
    // tenant has forbidden. The control plane resolves the job's policy and that is what reaches the provider.
    [Fact]
    public void AJobThatWithholdsReasoning_GetsNoSummaryEvenWhenTheRunnerAsksForOne()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(ReasoningEffort: "high", CaptureReasoning: true),
            captureReasoning: false);

        var raw = options!.RawRepresentationFactory!(new FakeNativeClient());
        var request = Assert.IsType<ProviderReasoningRequest>(raw);
        Assert.False(request.CaptureReasoning);

        // The effort level is a separate setting and reaches the provider as the runner sent it.
        Assert.Equal(ProviderReasoningEffort.High, request.Effort);
    }

    [Fact]
    public void AJobThatCapturesReasoning_GetsASummaryEvenWhenTheRunnerDidNotAskForOne()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(CaptureReasoning: false),
            captureReasoning: true);

        Assert.True(
            Assert.IsType<ProviderReasoningRequest>(options!.RawRepresentationFactory!(new FakeNativeClient()))
                .CaptureReasoning);

        // The same request in the form the OpenAI adapter reads. The capture flag alone would hold even if
        // the summary the provider is asked for went missing, and the summary is what returns the text.
        AssertRequestsAReasoningSummary(options);
    }

    // Without an effort level the shaping has nothing of its own to ask for, so the resolved refusal is what
    // the request has to state. An absent request would leave the runner's own opt-in standing, which is the
    // branch that opt-in would slip through.
    [Fact]
    public void AJobThatWithholdsReasoning_StatesTheRefusalWhenNoEffortIsSet()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(CaptureReasoning: true),
            captureReasoning: false);

        Assert.NotNull(options!.RawRepresentationFactory);

        var request = Assert.IsType<ProviderReasoningRequest>(options.RawRepresentationFactory!(new FakeNativeClient()));
        Assert.False(request.CaptureReasoning);
        Assert.Equal(ProviderReasoningEffort.None, request.Effort);
    }

    // A wire that named no reasoning at all is not a refusal either. The resolved answer decides, so a tenant
    // that captures gets the summary asked for on a call the runner sent nothing about.
    [Fact]
    public void AJobThatCapturesReasoning_GetsASummaryWhenTheWireNamedNoReasoning()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(MaxOutputTokens: 8192),
            captureReasoning: true);

        Assert.True(
            Assert.IsType<ProviderReasoningRequest>(options!.RawRepresentationFactory!(new FakeNativeClient()))
                .CaptureReasoning);
        AssertRequestsAReasoningSummary(options);
    }

    // A caller that has not resolved the job's policy must not change what the runner asked for.
    [Fact]
    public void WithoutAResolvedPolicy_TheRunnersOwnOptInStands()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(new RunnerChatOptions(CaptureReasoning: true));

        var raw = options!.RawRepresentationFactory!(new FakeNativeClient());
        Assert.True(Assert.IsType<ProviderReasoningRequest>(raw).CaptureReasoning);
    }

    // The OpenAI adapter reads the OpenAI library's own options object, and the summary verbosity is the
    // field on it that asks the provider to return the reasoning text.
    private static void AssertRequestsAReasoningSummary(ChatOptions options)
    {
        var raw = options.RawRepresentationFactory!(Substitute.For<IChatClient>());
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001
    }
}
