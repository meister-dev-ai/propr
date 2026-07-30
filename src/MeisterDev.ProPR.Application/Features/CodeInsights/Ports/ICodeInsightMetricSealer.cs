// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Seals the correctness measurement of a pull request when it finishes.
/// </summary>
/// <remarks>
///     Called from the same synchronization pass that observes pull-request state, alongside the disposition
///     consumer and the miss harvester. Best-effort like its siblings: behind the collection gate, and it never
///     throws back into the crawl.
/// </remarks>
public interface ICodeInsightMetricSealer
{
    /// <summary>
    ///     Seals the measurement for the pull request identified by <paramref name="key" />, over the findings
    ///     that had resolved by now. Does nothing when the collection gate is closed, when nothing was collected
    ///     for the pull request, or when it has already been sealed: the first close wins.
    /// </summary>
    /// <param name="key">The pull request that finished.</param>
    /// <param name="closeState">
    ///     The observed pull-request state, e.g. "Completed" or "Abandoned". Recorded, but it does not change
    ///     how the seal is computed: all close types seal identically.
    /// </param>
    /// <param name="ct">Cancels the seal.</param>
    /// <returns>Whether this call was the one that sealed the measurement.</returns>
    Task<bool> SealAsync(
        CodeInsightPullRequestKey key,
        string closeState,
        CancellationToken ct = default);
}
