// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Collapses duplicate review findings at the synthesis inlet. The default token-Jaccard implementation
///     preserves today's behavior; the semantic implementation merges same-file, overlapping-anchor findings of
///     the same defect class while keeping distinct bugs separate.
/// </summary>
public interface IFindingDeduplicator
{
    /// <summary>
    ///     Deduplicates the unioned per-file comment set for one review.
    /// </summary>
    /// <param name="comments">Comments gathered across all completed per-file results.</param>
    /// <param name="clientId">Client whose model binding governs any semantic judgment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ReviewComment>> DeduplicateAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        CancellationToken ct = default);
}
