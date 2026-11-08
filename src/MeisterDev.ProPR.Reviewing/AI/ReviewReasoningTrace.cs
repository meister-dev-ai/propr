// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Records on the trace of a review pass which reasoning level the places chose and which level the request names.
/// </summary>
/// <remarks>
///     A model receives only a level it accepts. When a stored level is no longer on the model's list, for example after
///     an operator pointed a logical model at another model, the request names the nearest lower level the model accepts
///     or no level, and the event records the substitution, so an operator sees why a pass reasoned less than configured.
///     Chat Completions receives only the level <c>none</c>; the event records the level the pass requests.
/// </remarks>
internal static class ReviewReasoningTrace
{
    /// <summary>Records the reasoning level of the pass that <paramref name="context" /> describes.</summary>
    /// <param name="context">The context of the pass, with its protocol, chosen level and model capabilities.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RecordAsync(ReviewSystemContext context, CancellationToken ct)
    {
        if (context.ActiveProtocolId is not { } protocolId || context.ProtocolRecorder is null)
        {
            return;
        }

        var requested = ReasoningLevelSelection.Normalize(context.ActiveReasoningLevel);
        var modelLevels = context.RuntimeCapabilities.ReasoningLevels;
        var sent = ReasoningLevelSelection.Sendable(requested, modelLevels);
        var details = JsonSerializer.Serialize(new { requestedLevel = requested, modelLevels });
        var output = sent is null
            ? JsonSerializer.Serialize(new { reasoningParameter = "omitted", substituted = requested is not null })
            : JsonSerializer.Serialize(new { reasoningParameter = "sent", level = sent, substituted = sent != requested });
        await context.ProtocolRecorder.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReasoningEffortSent,
            details,
            output,
            null,
            ct);
    }
}
