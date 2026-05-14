// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Explains why a review job selected its final review strategy.</summary>
public enum ReviewStrategySelectionSource
{
    /// <summary>No strategy was supplied, so the legacy-safe default was used.</summary>
    FallbackDefault,

    /// <summary>The strategy came from the client default configuration.</summary>
    ClientDefault,

    /// <summary>The strategy came from the review intake request.</summary>
    JobOverride,

    /// <summary>An unsupported or unavailable strategy fell back to the legacy-safe default.</summary>
    Fallback,
}
