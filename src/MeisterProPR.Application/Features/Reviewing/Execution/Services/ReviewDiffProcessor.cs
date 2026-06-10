// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Provides shared unified-diff processing utilities for Reviewing execution.
/// </summary>
public static class ReviewDiffProcessor
{
    /// <summary>
    ///     Classifies a changed file into a complexity tier based on changed-line count.
    ///     This identity is consumed by protocol and tier-selection paths, so threshold changes are behavioral changes.
    /// </summary>
    public static FileComplexityTier ClassifyTier(ChangedFile file)
    {
        var lines = CountChangedLines(file.UnifiedDiff);
        return lines switch
        {
            <= 30 => FileComplexityTier.Low,
            <= 150 => FileComplexityTier.Medium,
            _ => FileComplexityTier.High,
        };
    }

    /// <summary>
    ///     Counts added and removed content lines in a unified diff.
    ///     Hunk headers and file headers are excluded, so the count reflects only actual diff payload lines.
    /// </summary>
    public static int CountChangedLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in diff.AsSpan().EnumerateLines())
        {
            if (line.Length == 0)
            {
                continue;
            }

            var first = line[0];
            if (first is '+' or '-')
            {
                if (line.Length >= 3 && line[1] == first && line[2] == first)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Builds a lookup of inserted line numbers keyed by normalized review path for providers that only anchor to inserted lines.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<int>> BuildInsertedLineLookup(IReadOnlyList<ChangedFile> changedFiles)
    {
        var lookup = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            lookup[NormalizeReviewPath(changedFile.Path)] = ExtractInsertedNewLines(changedFile);
        }

        return lookup;
    }

    /// <summary>
    ///     Normalizes review paths into provider-comparable relative paths.
    /// </summary>
    public static string NormalizeReviewPath(string path)
    {
        return path.TrimStart('/');
    }

    private static HashSet<int> ExtractInsertedNewLines(ChangedFile changedFile)
    {
        var insertedLines = new HashSet<int>();
        var diffLines = changedFile.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;

        foreach (var diffLine in diffLines)
        {
            if (diffLine.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
                {
                    currentNewLine = newLineStart;
                    hasHunkHeader = true;
                }

                continue;
            }

            if (!hasHunkHeader)
            {
                continue;
            }

            if (diffLine.StartsWith("+++", StringComparison.Ordinal) ||
                diffLine.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (diffLine.StartsWith("+", StringComparison.Ordinal))
            {
                insertedLines.Add(currentNewLine);
                currentNewLine++;
                continue;
            }

            if (diffLine.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            currentNewLine++;
        }

        return insertedLines;
    }

    /// <summary>
    ///     Extracts merged, ascending new-file line ranges touched by each hunk in a unified diff.
    ///     Both added and context lines advance the new-file cursor; deletion-only hunks yield a single-line range.
    ///     Overlapping or adjacent ranges are merged. Returns an empty list when the diff is null, empty, or unparseable.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> ExtractChangedNewLineRanges(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return [];
        }

        var rawRanges = new List<(int Start, int End)>();
        var diffLines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;
        int? hunkStart = null;
        var hunkEnd = 0;

        foreach (var diffLine in diffLines)
        {
            if (diffLine.StartsWith("@@", StringComparison.Ordinal))
            {
                // Flush previous hunk range
                if (hunkStart.HasValue)
                {
                    rawRanges.Add((hunkStart.Value, Math.Max(hunkStart.Value, hunkEnd)));
                }

                hunkStart = null;
                hunkEnd = 0;

                if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
                {
                    currentNewLine = newLineStart;
                    hunkStart = currentNewLine;
                    hunkEnd = currentNewLine;
                    hasHunkHeader = true;
                }
                else
                {
                    hasHunkHeader = false;
                }

                continue;
            }

            if (!hasHunkHeader)
            {
                continue;
            }

            if (diffLine.StartsWith("+++", StringComparison.Ordinal) ||
                diffLine.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (diffLine.StartsWith("+", StringComparison.Ordinal))
            {
                // Added line — advances new-file cursor
                hunkEnd = currentNewLine;
                currentNewLine++;
                continue;
            }

            if (diffLine.StartsWith("-", StringComparison.Ordinal))
            {
                // Deleted line — does not advance new-file cursor; ensure range covers at least this position
                if (hunkStart.HasValue && currentNewLine > 0)
                {
                    hunkEnd = Math.Max(hunkEnd, currentNewLine);
                }

                continue;
            }

            // Context line — advances cursor
            hunkEnd = currentNewLine;
            currentNewLine++;
        }

        // Flush last hunk
        if (hunkStart.HasValue)
        {
            rawRanges.Add((hunkStart.Value, Math.Max(hunkStart.Value, hunkEnd)));
        }

        if (rawRanges.Count == 0)
        {
            return [];
        }

        // Merge overlapping/adjacent ranges (sort by start first)
        rawRanges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        var (ms, me) = rawRanges[0];
        for (var i = 1; i < rawRanges.Count; i++)
        {
            var (s, e) = rawRanges[i];
            if (s <= me + 1)
            {
                me = Math.Max(me, e);
            }
            else
            {
                merged.Add((ms, me));
                ms = s;
                me = e;
            }
        }

        merged.Add((ms, me));
        return merged;
    }

    private static bool TryParseUnifiedDiffNewLineStart(string diffLine, out int newLineStart)
    {
        newLineStart = 0;

        var plusIndex = diffLine.IndexOf('+');
        if (plusIndex < 0 || plusIndex + 1 >= diffLine.Length)
        {
            return false;
        }

        var endIndex = plusIndex + 1;
        while (endIndex < diffLine.Length && char.IsDigit(diffLine[endIndex]))
        {
            endIndex++;
        }

        return endIndex > plusIndex + 1
               && int.TryParse(diffLine[(plusIndex + 1)..endIndex], out newLineStart)
               && newLineStart > 0;
    }
}
