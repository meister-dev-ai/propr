// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Events;

/// <summary>
///     The scope a condition was evaluated at. Every part is non-null, the empty string standing in for "not
///     applicable", so a scope is always a complete key: the same reason the count projection does it.
/// </summary>
/// <param name="ClientId">The client. Always present.</param>
/// <param name="RepositoryId">Repository, or the empty string for a client-wide condition.</param>
/// <param name="FilePath">File, or the empty string for a condition that is not file-scoped.</param>
public sealed record CodeInsightEventScope(Guid ClientId, string RepositoryId, string FilePath)
{
    /// <summary>A client-wide scope.</summary>
    public static CodeInsightEventScope ForClient(Guid clientId)
    {
        return new CodeInsightEventScope(clientId, string.Empty, string.Empty);
    }

    /// <summary>A file-level scope within a repository.</summary>
    public static CodeInsightEventScope ForFile(Guid clientId, string? repositoryId, string? filePath)
    {
        return new CodeInsightEventScope(clientId, repositoryId ?? string.Empty, filePath ?? string.Empty);
    }
}

/// <summary>
///     Thresholds the quality conditions fire at. Uncalibrated on purpose: these are provisional defaults an
///     installation can move without a release, not settled numbers.
/// </summary>
/// <param name="WindowDays">How far back a condition looks.</param>
/// <param name="CorrectnessDeclineThreshold">
///     How far correctness must fall across the window before it is a transition rather than noise.
/// </param>
/// <param name="FalsePositiveShareThreshold">
///     The share of resolved findings judged wrong that counts as too noisy.
/// </param>
/// <param name="ConcentrationThreshold">Findings in one file within the window that counts as a hotspot.</param>
/// <param name="MinimumSealedPullRequests">
///     Sealed pull requests a correctness signal needs before it may fire. Without this, the first two closed
///     pull requests of a quiet week could raise an alert about the reviewer.
/// </param>
public sealed record CodeInsightConditionThresholds(
    int WindowDays,
    double CorrectnessDeclineThreshold,
    double FalsePositiveShareThreshold,
    int ConcentrationThreshold,
    int MinimumSealedPullRequests);
