// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Rewrites the provenance of mention answers that reached a pull request without it.
/// </summary>
/// <remarks>
///     Posting an answer and recording who posted it are two writes, and a process that dies between them used
///     to lose the second one for good: the answer stays on the pull request, the job is already complete, so
///     nothing retries it and nothing knows anything is missing. From then on the thread reads back as a
///     human's, because identity matching is all that is left to decide it.
///     <para>
///         The completed job now carries the comment id it posted, so the missing row is derivable from the
///         database alone: no provider call, no guessing which comment was ours. This reconciler is what
///         derives it. It is a recovery path, not a substitute for the write on the success path.
///     </para>
/// </remarks>
public interface IMentionReplyProvenanceReconciler
{
    /// <summary>
    ///     Records provenance for every recent mention answer that has none, and reports how many rows that
    ///     came to. Zero is the expected answer: it means nothing was lost.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<int> ReconcileAsync(CancellationToken ct = default);
}
