// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Why a rejected finding was rejected.
/// </summary>
/// <remarks>
///     <para>
///         The outcome says a finding was turned down. It does not say what to do about it, and the answers are
///         not related: a reviewer that invents problems needs a better prompt, one that argues with deliberate
///         decisions needs to read the codebase's conventions, and one that repeats another tool needs to be
///         told what that tool already covers. A precision number without these is a number nobody can act on.
///     </para>
///     <para>
///         Empirical work on AI review feedback found these five and found them very unevenly distributed, with
///         genuine mistakes making up well under half of all rejections. The set is deliberately theirs rather
///         than one of our own, so our numbers can be read beside published ones.
///     </para>
/// </remarks>
public enum CodeInsightRejectionReason
{
    // Persisted by ordinal: keep these values explicit and do NOT reorder or renumber, or historical reasons
    // would silently remap and every distribution computed from them would change meaning.

    /// <summary>
    ///     The finding did not describe a real problem. The reviewer misread the code, assumed something
    ///     untrue, or flagged something that is correct as it stands.
    /// </summary>
    Wrong = 0,

    /// <summary>
    ///     The finding was correct, and the code is the way it is on purpose. A trade-off the team made
    ///     knowingly and would make again.
    /// </summary>
    DesignTradeOff = 1,

    /// <summary>
    ///     The finding was correct, and the team simply prefers its own way. A matter of taste rather than of
    ///     consequence.
    /// </summary>
    DeveloperPreference = 2,

    /// <summary>
    ///     The finding was correct, and does not belong to this change. Pre-existing, or work the team tracks
    ///     somewhere else.
    /// </summary>
    OutOfScope = 3,

    /// <summary>
    ///     The finding was correct, and something else already covers it. Another tool, another finding, or a
    ///     comment already on the thread.
    /// </summary>
    Redundant = 4,
}
