// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

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
        public AiProtocolMode NativeProtocol => AiProtocolMode.Auto;

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
