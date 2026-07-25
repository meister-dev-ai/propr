// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Normalized verification result for one AI connection profile.
/// </summary>
public sealed record AiVerificationResultDto(
    AiVerificationStatus Status,
    AiVerificationFailureCategory? FailureCategory = null,
    string? Summary = null,
    string? ActionHint = null,
    DateTimeOffset? CheckedAt = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<string, string>? DriverMetadata = null)
{
    /// <summary>A reusable empty verification snapshot.</summary>
    public static AiVerificationResultDto NeverVerified { get; } =
        new(AiVerificationStatus.NeverVerified, null, null, null, null, []);
}
