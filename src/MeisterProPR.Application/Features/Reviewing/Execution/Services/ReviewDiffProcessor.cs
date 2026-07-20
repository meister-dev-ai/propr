// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The role of a single line that appears inside a unified-diff hunk body, as returned by
///     <see cref="ReviewDiffProcessor.ClassifyHunkLine" />.
/// </summary>
public enum HunkLineKind
{
    /// <summary>An added line (<c>+</c>); it occupies a new-file line, so it advances the new-file cursor.</summary>
    Added,

    /// <summary>A removed line (<c>-</c>); it occupies no new-file line, so it does not advance the cursor.</summary>
    Removed,

    /// <summary>An unchanged context line (space); present in the new file, so it advances the cursor.</summary>
    Context,

    /// <summary>
    ///     A non-payload line such as the <c>\ No newline at end of file</c> marker or a stray empty
    ///     element left by a trailing newline; it occupies no new-file line, so it does not advance the cursor.
    /// </summary>
    Marker,
}

/// <summary>
///     Provides shared unified-diff processing utilities for Reviewing execution.
/// </summary>
public static class ReviewDiffProcessor
{
    /// <summary>
    ///     Classifies a line that occurs inside a unified-diff hunk body by its first character alone.
    ///     Inside a hunk every line is payload: a leading <c>+</c> is an added line, <c>-</c> is removed,
    ///     a space is context, and anything else (for example the <c>\ No newline at end of file</c>
    ///     marker or a stray empty element) is a non-payload marker.
    ///     <para>
    ///     Classifying by the first character — rather than testing for the <c>+++</c>/<c>---</c>
    ///     file-header prefixes — is what lets an added line whose own content begins with <c>++</c>
    ///     (the C statement <c>++i;</c> renders in a diff as <c>+++i;</c>) be recognized as an addition
    ///     rather than a file header. This is exact only for single-file diffs, where the
    ///     <c>+++</c>/<c>---</c> file headers occur solely before the first hunk header; callers must
    ///     only pass lines that follow a hunk header. Concatenated multi-file diffs are outside this contract.
    ///     </para>
    /// </summary>
    public static HunkLineKind ClassifyHunkLine(string hunkLine)
    {
        if (string.IsNullOrEmpty(hunkLine))
        {
            return HunkLineKind.Marker;
        }

        return hunkLine[0] switch
        {
            '+' => HunkLineKind.Added,
            '-' => HunkLineKind.Removed,
            ' ' => HunkLineKind.Context,
            _ => HunkLineKind.Marker,
        };
    }

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
            ProcessDiffLineForInsertedLines(diffLine, insertedLines, ref hasHunkHeader, ref currentNewLine);
        }

        return insertedLines;
    }

    private static void ProcessDiffLineForInsertedLines(
        string diffLine,
        HashSet<int> insertedLines,
        ref bool hasHunkHeader,
        ref int currentNewLine)
    {
        if (diffLine.StartsWith("@@", StringComparison.Ordinal))
        {
            if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
            {
                currentNewLine = newLineStart;
                hasHunkHeader = true;
            }

            return;
        }

        if (!hasHunkHeader)
        {
            return;
        }

        switch (ClassifyHunkLine(diffLine))
        {
            case HunkLineKind.Added:
                insertedLines.Add(currentNewLine);
                currentNewLine++;
                break;
            case HunkLineKind.Context:
                currentNewLine++;
                break;
            case HunkLineKind.Removed:
            case HunkLineKind.Marker:
                // Removed lines and non-payload markers occupy no new-file line.
                break;
        }
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

        var rawRanges = BuildRawHunkRanges(unifiedDiff);
        return rawRanges.Count == 0 ? [] : MergeAdjacentRanges(rawRanges);
    }

    private static List<(int Start, int End)> BuildRawHunkRanges(string unifiedDiff)
    {
        var rawRanges = new List<(int Start, int End)>();
        var diffLines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;
        int? hunkStart = null;
        var hunkEnd = 0;

        foreach (var diffLine in diffLines)
        {
            ProcessDiffLineForHunkRanges(diffLine, rawRanges, ref hasHunkHeader, ref currentNewLine, ref hunkStart, ref hunkEnd);
        }

        // Flush last hunk
        if (hunkStart.HasValue)
        {
            rawRanges.Add((hunkStart.Value, Math.Max(hunkStart.Value, hunkEnd)));
        }

        return rawRanges;
    }

    private static void ProcessDiffLineForHunkRanges(
        string diffLine,
        List<(int Start, int End)> rawRanges,
        ref bool hasHunkHeader,
        ref int currentNewLine,
        ref int? hunkStart,
        ref int hunkEnd)
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

            return;
        }

        if (!hasHunkHeader)
        {
            return;
        }

        switch (ClassifyHunkLine(diffLine))
        {
            case HunkLineKind.Added:
            case HunkLineKind.Context:
                // Added and context lines both occupy a new-file line and advance the cursor.
                hunkEnd = currentNewLine;
                currentNewLine++;
                break;
            case HunkLineKind.Removed:
                // Deleted line — does not advance new-file cursor; ensure range covers at least this position.
                if (hunkStart.HasValue && currentNewLine > 0)
                {
                    hunkEnd = Math.Max(hunkEnd, currentNewLine);
                }

                break;
            case HunkLineKind.Marker:
                // Non-payload marker (e.g. "\ No newline at end of file") — occupies no new-file line.
                break;
        }
    }

    private static List<(int Start, int End)> MergeAdjacentRanges(List<(int Start, int End)> rawRanges)
    {
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

    /// <summary>
    ///     Renders a single-file unified diff with an explicit new-file line-number column: every
    ///     context and added payload line is prefixed with its 1-based line number in the new
    ///     version of the file ("N | "), removed lines keep a blank number column, and file/hunk
    ///     headers pass through untouched. Reviewer models read anchor line numbers from this
    ///     column instead of counting lines inside hunks. Inside a hunk every line is payload and
    ///     is classified by its first character only, so added/removed lines whose content itself
    ///     begins with "++" or "--" are never mistaken for file headers. Diffs without any
    ///     parseable hunk header are returned unchanged; concatenated multi-file diffs are outside
    ///     this method's contract.
    /// </summary>
    public static string AnnotateUnifiedDiffWithNewLineNumbers(string? unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return string.Empty;
        }

        var diffLines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var (entries, sawValidHunkHeader, maxNewLine) = BuildDiffEntries(diffLines);

        if (!sawValidHunkHeader)
        {
            return unifiedDiff;
        }

        return RenderAnnotatedDiff(unifiedDiff, entries, maxNewLine);
    }

    private static (List<(string Text, int? NewLineNumber, bool Annotate)> Entries, bool SawValidHunkHeader, int MaxNewLine)
        BuildDiffEntries(string[] diffLines)
    {
        var entries = new List<(string Text, int? NewLineNumber, bool Annotate)>(diffLines.Length);
        var hasHunkHeader = false;
        var sawValidHunkHeader = false;
        var currentNewLine = 0;
        var maxNewLine = 0;

        for (var index = 0; index < diffLines.Length; index++)
        {
            var diffLine = diffLines[index];

            // A trailing empty element from a final newline is structure, not a payload line.
            if (index == diffLines.Length - 1 && diffLine.Length == 0)
            {
                entries.Add((diffLine, null, false));
                continue;
            }

            if (diffLine.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
                {
                    currentNewLine = newLineStart;
                    hasHunkHeader = true;
                    sawValidHunkHeader = true;
                }
                else
                {
                    // An unparseable hunk header leaves the following payload without
                    // trustworthy coordinates; stop annotating until the next valid header
                    // rather than carry a stale cursor forward.
                    hasHunkHeader = false;
                }

                entries.Add((diffLine, null, false));
                continue;
            }

            // Outside a hunk only file headers and metadata occur; the "\ No newline at end of
            // file" marker is metadata wherever it appears.
            if (!hasHunkHeader || diffLine.StartsWith("\\", StringComparison.Ordinal))
            {
                entries.Add((diffLine, null, false));
                continue;
            }

            if (diffLine.StartsWith("-", StringComparison.Ordinal))
            {
                entries.Add((diffLine, null, true));
                continue;
            }

            entries.Add((diffLine, currentNewLine, true));
            maxNewLine = Math.Max(maxNewLine, currentNewLine);
            currentNewLine++;
        }

        return (entries, sawValidHunkHeader, maxNewLine);
    }

    private static string RenderAnnotatedDiff(
        string unifiedDiff,
        List<(string Text, int? NewLineNumber, bool Annotate)> entries,
        int maxNewLine)
    {
        // Deletion-only diffs render no new-file numbers but still get the blank column so the
        // annotated format stays uniform.
        var width = Math.Max(1, maxNewLine.ToString(CultureInfo.InvariantCulture).Length);
        var builder = new StringBuilder(unifiedDiff.Length + (entries.Count * (width + 3)));
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            var (text, newLineNumber, annotate) = entries[index];
            builder.Append(annotate ? FormatAnnotatedLine(newLineNumber, text, width) : text);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Prefixes every line of <paramref name="content" /> with its absolute 1-based file line
    ///     number ("N | "), starting at <paramref name="firstLineNumber" /> (clamped to 1). Used for
    ///     file-content slices handed to reviewer models so anchor line numbers are read from the
    ///     annotation instead of counted.
    /// </summary>
    public static string AnnotateContentWithLineNumbers(string? content, int firstLineNumber)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var start = Math.Max(1, firstLineNumber);
        var lines = content.Split('\n');

        // A trailing empty element from a final newline is structure, not an existing line; it
        // must neither be numbered nor widen the number column.
        var lineCount = lines.Length;
        var hasTrailingNewline = lineCount > 1 && lines[^1].Length == 0;
        if (hasTrailingNewline)
        {
            lineCount--;
        }

        var width = (start + lineCount - 1).ToString(CultureInfo.InvariantCulture).Length;
        var builder = new StringBuilder(content.Length + (lineCount * (width + 3)));

        for (var index = 0; index < lineCount; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(FormatAnnotatedLine(start + index, lines[index], width));
        }

        if (hasTrailingNewline)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Formats a single annotated line: a right-aligned line number (or a blank column when
    ///     <paramref name="lineNumber" /> is <see langword="null" />), the " | " separator, and the
    ///     unmodified line text.
    /// </summary>
    public static string FormatAnnotatedLine(int? lineNumber, string text, int width)
    {
        var column = lineNumber?.ToString(CultureInfo.InvariantCulture).PadLeft(width) ?? new string(' ', width);
        return $"{column} | {text}";
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
