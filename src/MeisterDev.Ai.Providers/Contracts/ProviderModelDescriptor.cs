// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     The model a runtime call is bound to, reduced to what a driver actually needs: which remote model to
///     address and which protocols it can speak. Capability metadata a host keeps for its own accounting stays
///     with the host.
/// </summary>
/// <param name="Id">Host-side identifier for the configured model, carried through for correlation.</param>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="SupportedProtocolModes">Protocol modes this model can serve.</param>
/// <param name="ReasoningContentField">
///     Field this model requires echoed back on assistant turns to preserve its chain of thought (DeepSeek-style
///     <c>reasoning_content</c>), or <see langword="null" /> when it has no such requirement. Carried here because
///     the descriptor is the only channel by which host-held model metadata reaches the library, so a normalizing
///     stage can act on the quirk without any model being named in code.
/// </param>
/// <param name="SupportsPromptCaching">
///     Whether this model can serve part of a prompt from the provider's cache. Carried for the same reason as
///     <paramref name="ReasoningContentField" />: it is per-model rather than per-provider, and a transport that
///     marks cache points has to know before it marks one. Marking a model that does not support caching is not a
///     missed optimisation but a rejected request, so the default is off and the host states it explicitly.
/// </param>
public sealed record ProviderModelDescriptor(
    Guid Id,
    string RemoteModelId,
    IReadOnlyList<AiProtocolMode> SupportedProtocolModes,
    string? ReasoningContentField = null,
    bool SupportsPromptCaching = false,
    bool SupportsReasoning = false);
