// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

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
    /// <returns>The surviving comments and one merge record for every comment that did not survive.</returns>
    Task<FindingDeduplicationResult> DeduplicateAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        CancellationToken ct = default);
}

/// <summary>
///     The result of one deduplication: the surviving comments in order, and the merges that removed the other
///     comments. The synthesis records the merges in the job protocol, so every comment that deduplication removes
///     after local verification can be traced to the comment that replaced it.
/// </summary>
/// <param name="Comments">The surviving comments.</param>
/// <param name="Merges">One record per merge group or per consolidation that removed at least one comment.</param>
public sealed record FindingDeduplicationResult(IReadOnlyList<ReviewComment> Comments, IReadOnlyList<FindingMergeRecord> Merges)
{
    /// <summary>Merge reason of two same-file comments that the merge judge found to describe the same defect.</summary>
    public const string SemanticSameDefectReason = "semantic_same_defect_class";

    /// <summary>Merge reason of comments that the token-similarity rules collapsed or consolidated across files.</summary>
    public const string TokenSimilarityReason = "token_similarity";

    /// <summary>
    ///     Builds the result of a deduplication step whose groups are not known, from its input and output. Every input
    ///     comment that is not in the output by reference is recorded as removed, together with the output comments
    ///     that are not in the input, which are the comments the step created in their place.
    /// </summary>
    /// <param name="input">The comments the step received.</param>
    /// <param name="output">The comments the step returned.</param>
    /// <param name="reason">The merge reason to record.</param>
    /// <param name="comparer">
    ///     How input and output comments are matched. The default matches by reference; a step that rebuilds the
    ///     comments it keeps needs a comparer over their content.
    /// </param>
    public static FindingDeduplicationResult FromDifference(
        IReadOnlyList<ReviewComment> input,
        IReadOnlyList<ReviewComment> output,
        string reason,
        IEqualityComparer<ReviewComment>? comparer = null)
    {
        comparer ??= ReferenceEqualityComparer.Instance;
        var kept = new HashSet<ReviewComment>(output, comparer);
        var received = new HashSet<ReviewComment>(input, comparer);
        var removed = input.Where(comment => !kept.Contains(comment)).ToList();
        if (removed.Count == 0)
        {
            return new FindingDeduplicationResult(output, []);
        }

        var created = output.Where(comment => !received.Contains(comment)).ToList();
        return new FindingDeduplicationResult(output, [new FindingMergeRecord(created, removed, reason)]);
    }
}

/// <summary>
///     One merge of a deduplication step. <paramref name="Kept" /> holds the comments that represent the merged group
///     after the step: the surviving member, or a consolidated comment the step created. It is empty when the step
///     does not report which comment replaced the removed ones.
/// </summary>
/// <param name="Kept">The comments that represent the group after the merge.</param>
/// <param name="Removed">The comments the merge removed.</param>
/// <param name="Reason">Why the comments were merged.</param>
public sealed record FindingMergeRecord(IReadOnlyList<ReviewComment> Kept, IReadOnlyList<ReviewComment> Removed, string Reason);
