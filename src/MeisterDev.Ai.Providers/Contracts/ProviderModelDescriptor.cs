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
public sealed record ProviderModelDescriptor(
    Guid Id,
    string RemoteModelId,
    IReadOnlyList<AiProtocolMode> SupportedProtocolModes);
