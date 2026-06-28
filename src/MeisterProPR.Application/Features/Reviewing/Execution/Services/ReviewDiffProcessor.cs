// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Provides shared unified-diff processing utilities for Reviewing execution.
/// </summary>
public static class ReviewDiffProcessor
{
    /// <summary>
    ///     Number of lines on either side of a changed range that still count as part of the change.
    ///     Context lines immediately adjacent to an edit are effectively touched by it, so a finding
    ///     within this neighborhood is treated as in-scope rather than pre-existing code.
    /// </summary>
    public const int AdjacentLineTolerance = 3;

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
    ///     Extracts only the added payload of a unified diff: the text of lines beginning with a single
    ///     <c>+</c> (excluding the <c>+++</c> file header), with the leading <c>+</c> removed. Matching against
    ///     this — rather than the raw diff — ensures file headers, hunk headers, context lines, and removed
    ///     lines can never produce a spurious content match.
    /// </summary>
    public static string ExtractAddedContent(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length > 0 && line[0] == '+')
            {
                builder.Append(line, 1, line.Length - 1).Append('\n');
            }
        }

        return builder.ToString();
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

            lookup[NormalizeReviewPath(changedFile.Path)] = GetInsertedNewLineNumbers(changedFile.UnifiedDiff);
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

    /// <summary>
    ///     Returns the 1-based new-file line numbers of inserted (<c>+</c>) lines in a unified diff,
    ///     excluding the <c>+++</c> file header. Empty when the diff is null, empty, or unparseable.
    /// </summary>
    public static HashSet<int> GetInsertedNewLineNumbers(string? unifiedDiff)
    {
        var insertedLines = new HashSet<int>();
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return insertedLines;
        }

        var diffLines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

    /// <summary>
    ///     Builds a lookup of merged changed-line ranges keyed by repository-relative file path, for use in
    ///     deterministic finding-scope classification. Binary files and files whose diff yields no resolvable
    ///     ranges are omitted, so findings on those files fall back to a <see langword="null" /> relation.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> BuildChangedLineRangesByPath(IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        // Keyed by NormalizeReviewPath + OrdinalIgnoreCase so AI-emitted finding paths (which can arrive
        // with a leading slash or different casing) match the repository-relative changed-file paths.
        var lookup = new Dictionary<string, IReadOnlyList<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            var ranges = ExtractChangedNewLineRanges(changedFile.UnifiedDiff);
            if (ranges.Count > 0)
            {
                lookup[NormalizeReviewPath(changedFile.Path)] = ranges;
            }
        }

        return lookup;
    }

    /// <summary>
    ///     Deterministically classifies a finding's anchor line against a file's merged changed-line ranges.
    ///     A line inside a range is <see cref="ChangedLineRelation.OnChangedLine" />; a line within
    ///     <see cref="AdjacentLineTolerance" /> of a range is <see cref="ChangedLineRelation.AdjacentToChange" />;
    ///     any other line is <see cref="ChangedLineRelation.OutsideChange" />. Returns <see langword="null" />
    ///     when the line is unknown or the file yielded no resolvable ranges, so such findings are never labeled.
    /// </summary>
    /// <param name="lineNumber">One-based new-file line number of the finding anchor, when known.</param>
    /// <param name="changedRanges">Merged, ascending changed-line ranges for the finding's file.</param>
    public static ChangedLineRelation? ClassifyChangedLineRelation(
        int? lineNumber,
        IReadOnlyList<(int Start, int End)>? changedRanges)
    {
        if (lineNumber is not { } line || changedRanges is null || changedRanges.Count == 0)
        {
            return null;
        }

        foreach (var (start, end) in changedRanges)
        {
            if (line >= start && line <= end)
            {
                return ChangedLineRelation.OnChangedLine;
            }
        }

        foreach (var (start, end) in changedRanges)
        {
            if (line >= start - AdjacentLineTolerance && line <= end + AdjacentLineTolerance)
            {
                return ChangedLineRelation.AdjacentToChange;
            }
        }

        return ChangedLineRelation.OutsideChange;
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
