// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     What actually became of a finding once its review thread resolved. This is the raw material for every
///     quality metric: the first three are cases where the finding was worth raising, the last is where it
///     was not.
/// </summary>
public enum CodeInsightDisposition
{
    // Persisted by ordinal: keep these values explicit and do NOT reorder or renumber, or historical
    // dispositions would silently remap and every metric computed from them would change meaning.

    /// <summary>
    ///     A fix was claimed and a corroborating code change was observed. The finding was right and acted on.
    /// </summary>
    Addressed = 0,

    /// <summary>
    ///     A human deliberately accepted the concern without changing the code, by design, won't fix. The
    ///     finding was right; the team chose not to act.
    /// </summary>
    Acknowledged = 1,

    /// <summary>
    ///     Disregarded, and judged correct but not relevant here. The finding was not wrong, it was unwanted.
    /// </summary>
    Dismissed = 2,

    /// <summary>Disregarded, and judged wrong. The reviewer was mistaken.</summary>
    FalsePositive = 3,

    /// <summary>
    ///     A human engaged with the finding and left it unresolved: a reply, an argument, or a question, and then
    ///     no verdict and no code change.
    /// </summary>
    /// <remarks>
    ///     Neither accepted nor rejected, and deliberately in neither ratio. Before this outcome existed the
    ///     classifier had to force such a thread into a rejection, which charged the reviewer for a verdict nobody
    ///     gave. Published work on AI review feedback found this case in 7.3 percent of threads, which is large
    ///     enough to move both lenses.
    /// </remarks>
    Discussed = 4,
}
