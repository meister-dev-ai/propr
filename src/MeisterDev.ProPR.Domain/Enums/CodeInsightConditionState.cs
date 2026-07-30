// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Which way a condition crossed. Every event is a <em>transition</em>, never a restatement: a condition
///     that stays true writes one <see cref="Firing" /> row and nothing further until it clears.
/// </summary>
/// <remarks>
///     Recording the clearing too is what makes fire-once implementable from this one table. The last row for a
///     scope and condition is the current state, so no separate bookkeeping table has to be kept in step, and a
///     consumer gets the recovery signal for free, which any alerting integration needs anyway.
/// </remarks>
public enum CodeInsightConditionState
{
    // Persisted by ordinal: do NOT reorder or renumber.

    /// <summary>The condition became true.</summary>
    Firing = 0,

    /// <summary>The condition stopped being true.</summary>
    Cleared = 1,
}
