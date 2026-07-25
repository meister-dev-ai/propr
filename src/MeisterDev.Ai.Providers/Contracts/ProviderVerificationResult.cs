// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Outcome of probing a provider for reachability and auth. Deliberately carries a specific cause and an
///     action hint rather than a bare boolean, so a misconfiguration can be reported at configuration time
///     instead of surfacing later as a failed workload.
/// </summary>
/// <param name="Status">Normalized verification status.</param>
/// <param name="FailureCategory">Category of failure when verification did not succeed.</param>
/// <param name="Summary">Short human-readable outcome.</param>
/// <param name="ActionHint">What an operator should change, when the cause suggests one.</param>
/// <param name="CheckedAt">When the probe ran.</param>
/// <param name="Warnings">Non-fatal notes about the attempt.</param>
/// <param name="DriverMetadata">Driver-specific detail worth surfacing, with no secret material.</param>
public sealed record ProviderVerificationResult(
    AiVerificationStatus Status,
    AiVerificationFailureCategory? FailureCategory = null,
    string? Summary = null,
    string? ActionHint = null,
    DateTimeOffset? CheckedAt = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<string, string>? DriverMetadata = null)
{
    /// <summary>A never-verified snapshot, used before any probe has run.</summary>
    public static ProviderVerificationResult NeverVerified { get; } =
        new(AiVerificationStatus.NeverVerified, null, null, null, null, []);
}
