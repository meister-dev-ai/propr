// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Provider-neutral classification of what a reviewer-owned thread's current state means for
///     memory, independent of any single SCM's status vocabulary. The crawl state machine maps each
///     provider's native thread status onto one of these so downstream logic never branches on
///     provider-specific strings.
/// </summary>
public enum ThreadResolutionIntent
{
    /// <summary>The thread is still open; no resolution to learn from yet.</summary>
    Active = 0,

    /// <summary>
    ///     A human deliberately accepted the concern without requiring a code change
    ///     (e.g. by-design or won't-fix). The acceptance itself is the resolution, so it is trustworthy
    ///     regardless of whether the code changed.
    /// </summary>
    AcceptedByHuman = 1,

    /// <summary>
    ///     The thread was closed as fixed/resolved, which only <em>claims</em> the concern was addressed.
    ///     The claim must be corroborated by an actual code change before it is trusted as memory.
    /// </summary>
    ClaimsFix = 2,
}
