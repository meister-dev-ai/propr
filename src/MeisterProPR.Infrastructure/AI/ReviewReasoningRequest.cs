// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Opts the outbound OpenAI Responses request into reasoning summaries so reasoning-capable models
///     (e.g. gpt-5.4, codex) actually return <c>TextReasoningContent</c> that the assistant-turn recorder can
///     capture. Without this, the request never asks for a summary and the reasoning field/viewer stays empty.
/// </summary>
internal static class ReviewReasoningRequest
{
    /// <summary>
    ///     When reasoning capture is enabled, sets the request to ask for an automatic reasoning summary via the
    ///     Responses API native options. The Microsoft.Extensions.AI OpenAI adapter builds its request on top of
    ///     the instance returned by <see cref="ChatOptions.RawRepresentationFactory" /> and leaves a pre-set
    ///     <c>ReasoningOptions</c> untouched, so this is the mechanism that reaches the wire as
    ///     <c>reasoning: { summary: "auto" }</c>. No-op when capture is disabled, and harmless for non-OpenAI
    ///     clients (they ignore <see cref="ChatOptions.RawRepresentationFactory" />). The reasoning effort level is
    ///     left unset so it stays governed by the selected model/deployment.
    /// </summary>
    public static ChatOptions ApplyReasoningSummaryOptIn(this ChatOptions chatOptions, bool captureReasoning)
    {
        if (!captureReasoning)
        {
            return chatOptions;
        }

        chatOptions.RawRepresentationFactory = _ =>
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
            new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto,
                },
            };
#pragma warning restore OPENAI001

        return chatOptions;
    }
}
