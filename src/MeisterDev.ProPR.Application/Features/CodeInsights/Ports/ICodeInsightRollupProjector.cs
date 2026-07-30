// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Keeps the daily count projection current as collected facts arrive.
/// </summary>
/// <remarks>
///     <para>
///         Every method <em>recomputes</em> the cells it touches from the source rows and writes the result,
///         rather than adding to them. Three separate events feed these counts (a finding being materialised,
///         classified, and having its outcome recorded) and a crawl or a retry can deliver any of them more
///         than once. An increment would double the count; a recomputation cannot, and a cell that has somehow
///         drifted repairs itself the next time anything touches it.
///     </para>
///     <para>
///         The recomputation is scoped to one job, so its cost is bounded by that job's finding count and is
///         independent of how much history exists.
///     </para>
///     <para>
///         Best-effort throughout, like every collection path: it never throws to its caller.
///     </para>
/// </remarks>
public interface ICodeInsightRollupProjector
{
    /// <summary>
    ///     Recomputes every projected cell for one review job: its finding total, its per-core-type counts,
    ///     and its per-outcome counts. Safe to call repeatedly and safe to call when the job has no findings
    ///     (its cells are then cleared rather than left stale).
    /// </summary>
    Task ProjectJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Projects up to <paramref name="maxJobs" /> review jobs whose findings have no projected cells at all,
    ///     and returns how many were projected. The catch-up path for findings collected before the projection
    ///     existed, and for any job whose projection was lost.
    /// </summary>
    /// <remarks>
    ///     Bounded per call and resumable by construction: the candidates are derived from what is missing, so a
    ///     sweep that stops halfway simply leaves fewer candidates for the next one. Only clients whose collection
    ///     gate is open are considered, so an opted-out client's backlog cannot occupy the batch forever.
    /// </remarks>
    Task<int> BackfillAsync(int maxJobs, CancellationToken ct = default);
}
