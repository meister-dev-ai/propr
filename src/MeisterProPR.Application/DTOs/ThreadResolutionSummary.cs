// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Result of summarizing a resolved PR review thread: the prose summary plus a classification
///     of how clearly the thread expresses a real resolution. Callers gate storage on
///     <see cref="Clarity" /> so threads with no determinable resolution are never persisted.
/// </summary>
/// <param name="Summary">AI-generated resolution summary. Never null or empty.</param>
/// <param name="Clarity">How clearly the thread expresses an actual resolution.</param>
public sealed record ThreadResolutionSummary(string Summary, ResolutionClarity Clarity)
{
    /// <summary>
    ///     Placeholder summary returned when generation fails. Always paired with
    ///     <see cref="ResolutionClarity.Undetermined" /> and never stored. This is the exact historical
    ///     string that leaked into the store before the clarity gate existed, so the one-time purge
    ///     migration matches on it.
    /// </summary>
    public const string GenerationFailedSummary =
        "Thread was resolved. No AI-generated summary could be produced at this time.";

    /// <summary>
    ///     True when the classification represents a genuine, determinable resolution worth storing
    ///     (<see cref="ResolutionClarity.ResolvedByChange" /> or
    ///     <see cref="ResolutionClarity.AcceptedWithoutChange" />).
    /// </summary>
    public bool IsStorable =>
        this.Clarity is ResolutionClarity.ResolvedByChange or ResolutionClarity.AcceptedWithoutChange;
}
