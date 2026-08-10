// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;

namespace MeisterDev.ProPR.Runner.Contracts;

/// <summary>
///     The portable slice of a relayed completion's options.
///     <para>
///         The review pipeline shapes every call with tools, a temperature, an output ceiling, and reasoning
///         settings. On the runner those live in a rich in-memory options object that cannot travel — tools
///         carry their implementations — so this record carries exactly the parts the provider needs to see:
///         each tool as a declaration (name, description, parameter schema) and the reasoning knobs in
///         neutral terms. A relay that dropped any of this would quietly turn a tool-using review into a
///         single-turn one, which is precisely what happened before the options rode the wire.
///     </para>
/// </summary>
/// <param name="Temperature">The sampling temperature, when the job pins one.</param>
/// <param name="MaxOutputTokens">The output ceiling for this call, when the caller resolved one.</param>
/// <param name="Tools">The tools the model may call, as declarations. Invocation stays with the runner.</param>
/// <param name="ReasoningEffort">
///     The reasoning effort level (<c>low</c> / <c>medium</c> / <c>high</c>), or null to leave the provider
///     at its default. Carried as text so the contract stays free of library enums.
/// </param>
/// <param name="CaptureReasoning">Whether the call asks the model to return a reasoning summary.</param>
public sealed record RunnerChatOptions(
    float? Temperature = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<RunnerChatToolDefinition>? Tools = null,
    string? ReasoningEffort = null,
    bool CaptureReasoning = false);

/// <summary>
///     One tool as the provider needs to see it: a name, a description, and a parameter schema. The
///     implementation never travels — the model's calls come back to the runner that offered the tool.
/// </summary>
/// <param name="Name">The tool name the model calls it by.</param>
/// <param name="Description">What the tool does, as shown to the model.</param>
/// <param name="Schema">The JSON schema of the tool's parameters, or null for a parameterless tool.</param>
public sealed record RunnerChatToolDefinition(
    string Name,
    string? Description = null,
    JsonElement? Schema = null);
