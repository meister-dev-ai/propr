// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Tracks the last-seen reply count for a single reviewer-owned comment thread on a pull request.
///     If the current reply count for a thread exceeds <see cref="LastSeenReplyCount" />, human replies
///     have been added and the system must generate a conversational response (FR-005 exception path).
///     Composite primary key: (<see cref="ReviewPrScanId" />, <see cref="ThreadId" />).
/// </summary>
public sealed class ReviewPrScanThread
{
    /// <summary>FK to the owning <see cref="ReviewPrScan" />.</summary>
    public Guid ReviewPrScanId { get; set; }

    /// <summary>ADO thread identifier within the pull request.</summary>
    public int ThreadId { get; set; }

    /// <summary>
    ///     The number of comments observed in this thread when it was last processed.
    ///     Compared against the current comment count to detect new replies.
    /// </summary>
    public int LastSeenReplyCount { get; set; }

    /// <summary>
    ///     The ADO thread status observed during the last crawl cycle (e.g. <c>"Active"</c>,
    ///     <c>"Fixed"</c>, <c>"WontFix"</c>, <c>"ByDesign"</c>).
    ///     <see langword="null" /> for records created before this feature was deployed.
    ///     Maximum 64 characters.
    /// </summary>
    public string? LastSeenStatus { get; set; }

    /// <summary>Navigation property back to the owning scan record.</summary>
    public ReviewPrScan ReviewPrScan { get; set; } = null!;
}
