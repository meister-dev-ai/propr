// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Records on the trace of a review pass with the reasoning effort None whether the pass sends the effort
///     <c>none</c> or leaves the effort out.
/// </summary>
/// <remarks>
///     A pass with the effort None sends <c>none</c> to a model that accepts it, on every transport. Any other model
///     receives no reasoning parameter and uses its own default, so the event shows an operator why a model
///     reasoned under the effort None. A pass with a configured Low, Medium or High records nothing, because Chat
///     Completions receives no such effort and the pass does not know its transport.
/// </remarks>
internal static class ReviewReasoningTrace
{
    /// <summary>Records the reasoning effort of the pass that <paramref name="context" /> describes.</summary>
    /// <param name="context">The context of the pass, with its protocol, configured effort and model capabilities.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RecordAsync(ReviewSystemContext context, CancellationToken ct)
    {
        if (context.ActiveReasoningEffort != ReviewReasoningEffort.None
            || context.ActiveProtocolId is not { } protocolId
            || context.ProtocolRecorder is null)
        {
            return;
        }

        var supportsReasoningEffortNone = context.RuntimeCapabilities.SupportsReasoningEffortNone;

        var details = JsonSerializer.Serialize(
            new
            {
                configuredEffort = "none",
                modelAcceptsEffortNone = supportsReasoningEffortNone,
            });
        var output = supportsReasoningEffortNone
            ? JsonSerializer.Serialize(new { reasoningParameter = "sent", effort = "none" })
            : JsonSerializer.Serialize(new { reasoningParameter = "omitted" });

        await context.ProtocolRecorder.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReasoningEffortSent,
            details,
            output,
            null,
            ct);
    }
}
