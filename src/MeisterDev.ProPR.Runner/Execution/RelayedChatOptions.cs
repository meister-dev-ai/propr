// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Flattens the chat options the review pipeline built into the wire shape the relay accepts.
///     <para>
///         The pipeline hands the relay client the same options object it hands a provider client: tools
///         with implementations attached, and reasoning settings hidden inside a per-client factory. Neither
///         travels as-is. Tools go as declarations, because the model's calls come back here to be invoked,
///         and the reasoning settings are recovered by asking that factory the same question a
///         native-protocol driver would ask it.
///     </para>
/// </summary>
internal static class RelayedChatOptions
{
    /// <summary>The wire form of the options, or null when there is nothing to carry.</summary>
    /// <param name="options">The options the pipeline built for this call.</param>
    public static RunnerChatOptions? FromChatOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var tools = options.Tools?.OfType<AIFunction>()
            .Select(tool => new RunnerChatToolDefinition(tool.Name, tool.Description, tool.JsonSchema))
            .ToList();

        string? effort = null;
        var captureReasoning = false;

        // The reasoning factory answers a native-protocol client with the neutral request; posing as one
        // recovers the effort and capture knobs without naming a provider.
        if (options.RawRepresentationFactory?.Invoke(ReasoningProbe.Instance) is ProviderReasoningRequest reasoning)
        {
            effort = reasoning.Effort == ProviderReasoningEffort.None
                ? null
                : reasoning.Effort.ToString().ToLowerInvariant();
            captureReasoning = reasoning.CaptureReasoning;
        }

        if (options.Temperature is null
            && options.MaxOutputTokens is null
            && tools is not { Count: > 0 }
            && effort is null
            && !captureReasoning)
        {
            return null;
        }

        return new RunnerChatOptions(
            options.Temperature,
            options.MaxOutputTokens,
            tools is { Count: > 0 } ? tools : null,
            effort,
            captureReasoning);
    }

    private sealed class ReasoningProbe : INativeProtocolChatClient
    {
        public static readonly ReasoningProbe Instance = new();

        public AiProtocolMode NativeProtocol => AiProtocolMode.Auto;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The reasoning probe never performs a completion.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The reasoning probe never performs a completion.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            // Stateless singleton; nothing to release.
        }
    }
}
