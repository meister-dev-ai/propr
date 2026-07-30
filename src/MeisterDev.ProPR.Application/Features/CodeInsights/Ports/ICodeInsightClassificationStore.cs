// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Persistence boundary for what kind of problem each collected finding is, and for the backlog of findings
///     still waiting to be told.
/// </summary>
/// <remarks>
///     Separate from <see cref="ICodeInsightFindingStore" /> because classification arrives after the fact and on
///     its own schedule: a sweeper drains the backlog, a review view reads the result, and neither has any business
///     with materialising findings.
/// </remarks>
public interface ICodeInsightClassificationStore
{
    /// <summary>
    ///     Returns the classification of every finding collected for one review job, ordered by the position
    ///     the finding held in that job's review result. Empty when the job produced nothing, or when nothing
    ///     was collected for it (the client had not opted in at the time).
    ///     This is the read a review view uses to line tags up against the findings it already renders.
    /// </summary>
    Task<IReadOnlyList<CodeInsightFindingClassificationView>> GetClassificationsForJobAsync(
        Guid jobId,
        int maxClassificationAttempts,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="limit" /> findings still awaiting type classification whose attempt
    ///     count is below <paramref name="maxAttempts" />, oldest first so a backlog drains in the order it
    ///     accumulated. A finding that has exhausted its attempts is never returned again, which is what stops
    ///     a permanently unclassifiable finding from being retried forever.
    /// </summary>
    Task<IReadOnlyList<CodeInsightUnclassifiedFinding>> ListUnclassifiedAsync(
        int limit,
        int maxAttempts,
        CancellationToken ct = default);

    /// <summary>
    ///     Records <paramref name="classification" /> against the finding, replacing any tags a previous
    ///     attempt assigned so a retry cannot double-count, and marks it classified. Idempotent: applying the
    ///     same classification twice leaves one set of tags.
    /// </summary>
    Task ApplyClassificationAsync(
        Guid findingId,
        CodeInsightClassification classification,
        CancellationToken ct = default);

    /// <summary>
    ///     Records that a classification attempt was made and did not succeed, so the finding is retried on a
    ///     later sweep until it exhausts its attempts.
    /// </summary>
    Task RecordClassificationAttemptAsync(Guid findingId, CancellationToken ct = default);

    /// <summary>
    ///     Returns how many findings are still awaiting classification and remain retryable: the backlog
    ///     depth, reported as a diagnostic so an unbounded backlog is visible rather than inferred.
    /// </summary>
    Task<int> CountUnclassifiedAsync(int maxAttempts, CancellationToken ct = default);
}
