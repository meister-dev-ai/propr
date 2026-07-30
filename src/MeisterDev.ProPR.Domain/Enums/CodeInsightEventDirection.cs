// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Which way the measured value moved. Deliberately factual rather than judgemental: whether a rise is good
///     or bad depends on the metric, and that is the consumer's business.
/// </summary>
public enum CodeInsightEventDirection
{
    // Persisted by ordinal: do NOT reorder or renumber.

    /// <summary>The value rose.</summary>
    Rose = 0,

    /// <summary>The value fell.</summary>
    Fell = 1,
}
