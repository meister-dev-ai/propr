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
            var group = new List<ReviewComment> { anchor };

            for (var j = i + 1; j < fileLevelComments.Count; j++)
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

            if (group.Count > 1)
            {
                // Build a consolidated PR-level comment listing all affected files.
                var affectedFiles = group
                    .Select(c => c.FilePath!)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var fileList = string.Join(", ", affectedFiles);
                var consolidatedMessage =
                    $"[Cross-file] {anchor.Message} — Affected files: {fileList}";

                merged.Add(new ReviewComment(null, null, anchor.Severity, consolidatedMessage));
                consumed.Add(i);
            }
            else
            {
                // Unique finding — keep as-is.
                merged.Add(anchor);
                consumed.Add(i);
            }
        }

        // Re-append PR-level pass-through comments at the end.
        merged.AddRange(prLevelComments);

        return merged.AsReadOnly();
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
