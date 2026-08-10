// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     The only persistence the review pipeline itself performs: reading a job with what it has already
///     reviewed, and recording each file as it finishes.
///     <para>
///         Narrow on purpose. The pipeline previously took <c>IJobRepository</c>, whose thirty-eight
///         methods span intake, history, budgeting, and lifecycle, and used four of them. On the control
///         plane that was merely over-broad; for an executor with no database it is the difference between
///         implementing four methods and stubbing thirty-four it must never be asked for.
///     </para>
///     <para>
///         Same classification the tool surface got: name what a collaborator actually needs, so the two
///         bindings differ in where the work lands and not in what the pipeline can do.
///     </para>
/// </summary>
public interface IReviewFileResultStore
{
    /// <summary>
    ///     The job together with the per-file results already recorded for it, which is what lets a resumed
    ///     job skip the files it finished rather than re-reviewing and re-paying for them.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a file's first result.</summary>
    /// <param name="result">The result.</param>
    /// <param name="ct">The cancellation token.</param>
    Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    /// <summary>Replaces a file's result, which a retry of that file produces.</summary>
    /// <param name="result">The result.</param>
    /// <param name="ct">The cancellation token.</param>
    Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default);

    /// <summary>
    ///     Records how many in-scope files this iteration has, which is the denominator behind the
    ///     "X of Y files reviewed" progress an operator watches.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="count">The in-scope changed-file count.</param>
    /// <param name="ct">The cancellation token.</param>
    Task UpdateInScopeChangedFileCountAsync(Guid id, int count, CancellationToken ct = default);
}
