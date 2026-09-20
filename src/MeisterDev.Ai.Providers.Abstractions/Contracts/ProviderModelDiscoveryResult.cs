// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Outcome of asking a provider which models it exposes. A provider that cannot be enumerated is not a
///     failure: it reports that manual entry is allowed so the host can still let an operator name a model.
/// </summary>
/// <param name="DiscoveryStatus">Normalized status token for the attempt.</param>
/// <param name="ManualEntryAllowed">Whether the host should still permit hand-entered models.</param>
/// <param name="Warnings">Non-fatal notes about the attempt.</param>
/// <param name="Models">Models the provider reported.</param>
public sealed record ProviderModelDiscoveryResult(
    string DiscoveryStatus,
    bool ManualEntryAllowed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProviderDiscoveredModel> Models);
