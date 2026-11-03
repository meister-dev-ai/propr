// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Outcome of asking a provider which models it exposes. A provider that cannot be enumerated is not a
///     failure: it reports that manual entry is allowed so the host can still let an operator name a model.
/// </summary>
/// <param name="DiscoveryStatus">Normalized status token for the attempt.</param>
/// <param name="ManualEntryAllowed">Whether the host should still permit hand-entered models.</param>
/// <param name="Warnings">Non-fatal notes about the attempt.</param>
/// <param name="Models">Models the provider reported.</param>
/// <remarks>
///     The constructor and the deconstruction take the four values of contract 1.1. <see cref="FailureCategory" />
///     and <see cref="HttpStatus" /> are properties so that a family built against 1.1 binds to this record.
/// </remarks>
public sealed record ProviderModelDiscoveryResult(
    string DiscoveryStatus,
    bool ManualEntryAllowed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProviderDiscoveredModel> Models)
{
    /// <summary>Why a failed attempt failed, in the categories of a verification, or null.</summary>
    public AiVerificationFailureCategory? FailureCategory { get; init; }

    /// <summary>The status a failed attempt was answered with, or null without one.</summary>
    public int? HttpStatus { get; init; }
}
