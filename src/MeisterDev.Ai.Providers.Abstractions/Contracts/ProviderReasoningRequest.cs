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
public sealed record ProviderReasoningRequest(ProviderReasoningEffort Effort, bool CaptureReasoning);
