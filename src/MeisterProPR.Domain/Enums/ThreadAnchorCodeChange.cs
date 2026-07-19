// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Whether the code a review thread was anchored to has changed since the thread's finding was
///     first raised. Determined at the provider boundary (e.g. an "outdated" diff hunk, or the anchored
///     file changing in a later iteration). Used to decide whether a "fixed" close is corroborated by an
///     actual code change before it is remembered as suppression memory.
/// </summary>
public enum ThreadAnchorCodeChange
{
    /// <summary>
    ///     The provider could not determine whether the anchored code changed. Treated conservatively:
    ///     a claimed fix is not trusted when the signal is unavailable.
    /// </summary>
    Unknown = 0,

    /// <summary>The anchored code changed after the finding was raised — a claimed fix is corroborated.</summary>
    Changed = 1,

    /// <summary>The anchored code did not change — a "fixed" close is not backed by a code change.</summary>
    Unchanged = 2,
}
