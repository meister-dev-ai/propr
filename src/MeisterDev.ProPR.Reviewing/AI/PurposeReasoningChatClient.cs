// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Applies the reasoning level of a purpose to every call of its runtime that states no reasoning request of its own.
/// </summary>
/// <remarks>
///     A review pass, the PR-wide strategy and the evidence judge choose their level per call and state it on the
///     request, so this client leaves those calls as they are. Every other call of the purpose, for example triage, a
///     summary or a memory decision, runs at the level the purpose assignment, the logical model or the configured model
///     sets, restricted to the levels the model accepts.
/// </remarks>
/// <param name="inner">The client of the resolved runtime.</param>
/// <param name="level">The level of the purpose.</param>
/// <param name="acceptedLevels">The levels the model accepts, or null when no source knows them.</param>
/// <param name="usesResponsesApi">Whether the model is called through the Responses API.</param>
internal sealed class PurposeReasoningChatClient(
    IChatClient inner,
    string level,
    IReadOnlyList<string>? acceptedLevels,
    bool usesResponsesApi) : DelegatingChatClient(inner)
{
    /// <summary>The level this client applies.</summary>
    public string Level => level;

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(messages, this.Shape(options), cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(messages, this.Shape(options), cancellationToken);
    }

    // A call that states a reasoning request or the shared setting keeps it. The options are copied before they are
    // shaped, because the caller may send the same options to another model. When the model accepts no level the
    // purpose sets, the call goes out as the caller made it.
    private ChatOptions? Shape(ChatOptions? options)
    {
        if (options is not null && (options.CarriesAReasoningRequest() || options.Reasoning is not null))
        {
            return options;
        }

        var shaped = (options?.Clone() ?? new ChatOptions()).ApplyReasoning(false, level, acceptedLevels, usesResponsesApi);
        return shaped.CarriesAReasoningRequest() ? shaped : options;
    }
}
