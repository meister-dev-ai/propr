// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     How clearly a resolved PR review thread expresses an actual resolution, used to decide
///     whether the thread is worth storing as memory. Threads whose outcome cannot be tied to a
///     genuine resolution are not stored, so speculative or empty "resolutions" never reach a
///     future review.
/// </summary>
public enum ResolutionClarity
{
    /// <summary>A code change resolved the concern. Worth storing.</summary>
    ResolvedByChange = 0,

    /// <summary>
    ///     Explicitly acknowledged as intentional, by-design, or accepted without a code change.
    ///     Worth storing.
    /// </summary>
    AcceptedWithoutChange = 1,

    /// <summary>Closed with no actual conclusion. Not stored.</summary>
    ClosedWithoutResolution = 2,

    /// <summary>
    ///     The resolution could not be determined from the available context, including when
    ///     summary generation failed. Not stored.
    /// </summary>
    Undetermined = 3,
}
