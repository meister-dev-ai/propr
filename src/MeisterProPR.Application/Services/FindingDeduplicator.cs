// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Deduplicates cross-file review findings using token-set Jaccard similarity.
///     When the same root-cause concern appears across multiple files, the individual
///     per-file comments are collapsed into a single PR-level comment (<c>FilePath = null</c>)
///     that lists all affected file paths.
/// </summary>
public static class FindingDeduplicator
{
    /// <summary>
    ///     Jaccard similarity threshold above which two comments are considered duplicates.
    ///     0.50 = 50% domain-token overlap required (stop words excluded).
    /// </summary>
    private const double JaccardThreshold = 0.50;

    /// <summary>
    ///     Jaccard similarity threshold above which two findings anchored to the SAME file are treated as
    ///     one finding reported twice (for example by a baseline pass and a verification pass). Set higher
    ///     than <see cref="JaccardThreshold" /> so that distinct findings on different lines of one file are
    ///     preserved and only near-identical restatements collapse.
    /// </summary>
    private const double SameFileDuplicateThreshold = 0.72;

    /// <summary>
    ///     Stop words excluded from Jaccard tokenisation so that common grammatical words
    ///     do not inflate similarity scores between semantically different messages.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "be", "been", "being",
        "in", "of", "to", "for", "on", "at", "by", "or", "and", "but",
        "this", "that", "it", "its", "if", "as", "with", "from",
        "not", "may", "can", "should", "could", "would", "will",
        "you", "your",
    };

    /// <summary>
    ///     Deduplicates a list of review comments by collapsing cross-file findings with
    ///     the same root pattern into a single consolidated comment.
    ///     Comments with <c>FilePath = null</c> (PR-level) are never used as merge sources
    ///     and pass through unchanged. Same-file comments are never merged.
    ///     Comments with different severities are never merged.
    /// </summary>
    /// <param name="comments">Input comments from the review orchestrator.</param>
    /// <returns>Deduplicated list; order is deterministic but not necessarily preserved.</returns>
    public static IReadOnlyList<ReviewComment> Deduplicate(IReadOnlyList<ReviewComment> comments)
    {
        if (comments.Count <= 1)
        {
            return comments;
        }

        // Only comments with a non-null FilePath can participate in cross-file deduplication.
        var fileLevelComments = comments.Where(c => c.FilePath is not null).ToList();
        var prLevelComments = comments.Where(c => c.FilePath is null).ToList();

        var merged = new List<ReviewComment>();
        var consumed = new HashSet<int>();

        for (var i = 0; i < fileLevelComments.Count; i++)
        {
            if (consumed.Contains(i))
            {
                continue;
            }

            var anchor = fileLevelComments[i];
            var group = CollectCrossFileDuplicateGroup(fileLevelComments, i, anchor, consumed);

            merged.Add(group.Count > 1 ? BuildConsolidatedComment(anchor, group) : anchor);
            consumed.Add(i);
        }

        // Re-append PR-level pass-through comments at the end.
        merged.AddRange(prLevelComments);

        return merged.AsReadOnly();
    }

    // Scans the remaining not-yet-consumed comments for near-duplicates of the anchor (same
    // severity, different file, message similarity over the cross-file threshold), marking each
    // match consumed so it is not considered as its own group anchor later.
    private static List<ReviewComment> CollectCrossFileDuplicateGroup(
        IReadOnlyList<ReviewComment> fileLevelComments,
        int anchorIndex,
        ReviewComment anchor,
        HashSet<int> consumed)
    {
        var group = new List<ReviewComment> { anchor };

        for (var j = anchorIndex + 1; j < fileLevelComments.Count; j++)
        {
            if (consumed.Contains(j))
            {
                continue;
            }

            var candidate = fileLevelComments[j];

            // Never merge same-file comments or different-severity comments.
            if (candidate.FilePath == anchor.FilePath || candidate.Severity != anchor.Severity)
            {
                continue;
            }

            if (JaccardSimilarity(anchor.Message, candidate.Message) >= JaccardThreshold)
            {
                group.Add(candidate);
                consumed.Add(j);
            }
        }

        return group;
    }

    // Builds a consolidated PR-level comment listing all affected files.
    private static ReviewComment BuildConsolidatedComment(ReviewComment anchor, List<ReviewComment> group)
    {
        var affectedFiles = group
            .Select(c => c.FilePath!)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileList = string.Join(", ", affectedFiles);
        var consolidatedMessage = $"[Cross-file] {anchor.Message} — Affected files: {fileList}";

        return new ReviewComment(null, null, anchor.Severity, consolidatedMessage);
    }

    /// <summary>
    ///     Collapses near-identical findings anchored to the SAME file into a single comment, keeping the
    ///     first occurrence. Two comments collapse when they share a file path and severity and their
    ///     messages exceed <see cref="SameFileDuplicateThreshold" /> token-set Jaccard similarity — the
    ///     situation that arises when more than one review pass independently reports the same issue on the
    ///     same file. Distinct findings on different lines survive because their messages do not clear the
    ///     high similarity bar. This complements <see cref="Deduplicate" />, which merges across files and
    ///     deliberately never touches same-file findings. Input order is preserved.
    /// </summary>
    /// <param name="comments">Input comments from the review orchestrator.</param>
    /// <returns>The input list with same-file near-duplicates removed.</returns>
    public static IReadOnlyList<ReviewComment> CollapseSameFileDuplicates(IReadOnlyList<ReviewComment> comments)
    {
        if (comments.Count <= 1)
        {
            return comments;
        }

        var kept = new List<ReviewComment>(comments.Count);
        foreach (var comment in comments)
        {
            var isDuplicate = comment.FilePath is not null
                              && kept.Any(existing =>
                                  existing.FilePath is not null
                                  && string.Equals(existing.FilePath, comment.FilePath, StringComparison.OrdinalIgnoreCase)
                                  && existing.Severity == comment.Severity
                                  && JaccardSimilarity(existing.Message, comment.Message) >= SameFileDuplicateThreshold);
            if (!isDuplicate)
            {
                kept.Add(comment);
            }
        }

        return kept.AsReadOnly();
    }

    /// <summary>
    ///     Computes the token-set Jaccard similarity between two strings.
    ///     Tokens are lower-cased words with punctuation stripped.
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <returns>Jaccard similarity in [0, 1].</returns>
    internal static double JaccardSimilarity(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
        {
            // Both messages have no domain tokens — not meaningfully similar.
            return 0.0;
        }

        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0.0;
        }

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        return (double)intersection / union;
    }

    /// <summary>
    ///     Splits a message into a deduplicated set of normalised tokens (lower-case, letters and digits only).
    /// </summary>
    /// <param name="text">Input text.</param>
    /// <returns>Set of tokens.</returns>
    internal static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'');

            if (!isWordChar)
            {
                if (i > start)
                {
                    var token = text[start..i].ToLowerInvariant();
                    if (token.Length >= 3 && !StopWords.Contains(token))
                    {
                        tokens.Add(token);
                    }
                }

                start = i + 1;
            }
        }

        return tokens;
    }
}
