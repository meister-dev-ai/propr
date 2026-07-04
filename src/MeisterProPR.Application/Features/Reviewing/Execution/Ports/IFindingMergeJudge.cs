// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Decides whether two candidate findings describe the same defect class. This is the single judgment step
///     of the conservative semantic merge: the deduplicator only consults it after the deterministic pre-filters
///     (same file, overlapping anchor) already hold, so a judge that returns <see langword="false" /> — including
///     the deterministic no-op fallback used when no model is bound — can only ever keep two findings separate,
///     never merge distinct bugs.
/// </summary>
public interface IFindingMergeJudge
{
    /// <summary>
    ///     Returns <see langword="true" /> only when the two findings describe the same underlying defect and may
    ///     therefore be collapsed into one. Returns <see langword="false" /> for distinct defects (even with
    ///     overlapping vocabulary) and whenever the judgment cannot be made.
    /// </summary>
    /// <param name="first">First candidate finding.</param>
    /// <param name="second">Second candidate finding.</param>
    /// <param name="clientId">Client whose model binding governs the judgment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> AreSameDefectClassAsync(
        CandidateReviewFinding first,
        CandidateReviewFinding second,
        Guid clientId,
        CancellationToken ct = default);
}
