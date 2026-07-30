// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Which quality condition a <see cref="Entities.CodeInsightEvent" /> records a transition of.
/// </summary>
public enum CodeInsightEventType
{
    // Persisted by ordinal: do NOT reorder or renumber, or historical events would silently change meaning.

    /// <summary>Correctness fell across the evaluated window by more than the configured amount.</summary>
    CorrectnessDeclining = 0,

    /// <summary>
    ///     The share of resolved findings judged wrong rose above the configured share. Distinct from
    ///     correctness falling: precision can hold while the reviewer becomes noisier in absolute terms.
    /// </summary>
    FalsePositiveShareHigh = 1,

    /// <summary>
    ///     One file accumulated more findings in the window than the configured threshold: a hotspot worth
    ///     looking at rather than a reviewer problem.
    /// </summary>
    ConcentrationHotspot = 2,
}
