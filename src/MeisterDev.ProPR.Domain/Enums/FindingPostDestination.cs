// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     The place a finding of one finding type takes on the pull request. The values are ordered from the most visible
///     destination to the least visible one, because a finding with several types takes the most visible destination
///     among its types.
/// </summary>
public enum FindingPostDestination
{
    // Persisted by name and compared by value: keep the values in the order of visibility.

    /// <summary>ProPR posts the finding as a comment at its line.</summary>
    Comment = 0,

    /// <summary>ProPR posts no comment for the finding and lists it in the review summary.</summary>
    Summary = 1,

    /// <summary>
    ///     ProPR neither posts nor lists the finding. The finding stays in the ProPR review, and the review summary
    ///     states how many findings were kept there.
    /// </summary>
    ReviewOnly = 2,
}
