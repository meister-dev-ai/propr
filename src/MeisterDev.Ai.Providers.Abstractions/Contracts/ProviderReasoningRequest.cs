// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     What the caller wants of a model's reasoning, in provider-neutral terms.
/// </summary>
/// <remarks>
///     Travels as the <see cref="ChatOptions.RawRepresentationFactory" /> value for clients that speak a native
///     protocol, which is the one channel Microsoft.Extensions.AI leaves open for per-client request shaping. The
///     caller says how much reasoning it wants and whether it wants to see it; the driver decides what that means
///     on its own wire.
/// </remarks>
/// <param name="Effort">How much reasoning to ask for.</param>
/// <param name="CaptureReasoning">
///     Whether the caller wants the reasoning itself back, where the provider makes that a separate choice from
///     doing the reasoning at all.
/// </param>
public sealed record ProviderReasoningRequest(ProviderReasoningEffort Effort, bool CaptureReasoning)
{
    /// <summary>
    ///     The reasoning level by the provider's name, for example <c>max</c>, or null when the caller states none.
    /// </summary>
    /// <remarks>
    ///     The host sends only a level the model accepts. <see cref="Effort" /> carries the nearest neutral value for
    ///     a family that does not read the name: <c>none</c> as None, <c>minimal</c> and <c>low</c> as Low, <c>medium</c>
    ///     as Medium, and <c>high</c>, <c>xhigh</c> and <c>max</c> as High.
    /// </remarks>
    public string? Level { get; init; }
}
