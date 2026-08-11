// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Rebuilds a relayed completion's options on the side that holds the provider client.
///     <para>
///         The runner sent tools as declarations and the reasoning settings in neutral terms. Tools become
///         declaration-only functions: the provider serializes their name and schema into the request, and
///         the model's calls travel back to the runner that owns the implementations. Reasoning goes back
///         through the same per-client shaping the in-process path uses, because the actual provider client
///         is known only here.
///     </para>
/// </summary>
public static class RunnerRelayedChatOptions
{
    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

    /// <summary>The options to hand the resolved client, or null when the runner sent none.</summary>
    /// <param name="wire">The options as they came off the wire.</param>
    public static ChatOptions? ToChatOptions(RunnerChatOptions? wire)
    {
        if (wire is null)
        {
            return null;
        }

        var options = new ChatOptions
        {
            Temperature = wire.Temperature,
            MaxOutputTokens = wire.MaxOutputTokens,
            Tools = wire.Tools is { Count: > 0 } tools
                ? [.. tools.Select(AITool (tool) => new RelayedToolDeclaration(tool))]
                : null,
        };

        return options.ApplyReasoning(wire.CaptureReasoning, ParseEffort(wire.ReasoningEffort));
    }

    private static ReviewReasoningEffort ParseEffort(string? effort)
    {
        // Unknown values fall back to None rather than failing the call: a newer runner naming a level this
        // build does not know should still get its completion, at the provider's default effort.
        return effort?.ToLowerInvariant() switch
        {
            "low" => ReviewReasoningEffort.Low,
            "medium" => ReviewReasoningEffort.Medium,
            "high" => ReviewReasoningEffort.High,
            _ => ReviewReasoningEffort.None,
        };
    }

    private sealed class RelayedToolDeclaration(RunnerChatToolDefinition definition) : AIFunction
    {
        public override string Name => definition.Name;

        public override string Description => definition.Description ?? string.Empty;

        public override JsonElement JsonSchema => definition.Schema ?? EmptyObjectSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // The implementation lives on the runner. Nothing on the control plane invokes tools, because
            // the resolved clients carry no invocation wrapper, so reaching this line means a composition
            // bug.
            throw new NotSupportedException($"Tool '{definition.Name}' is a relayed declaration; it is invoked on the runner that offered it.");
        }
    }
}
