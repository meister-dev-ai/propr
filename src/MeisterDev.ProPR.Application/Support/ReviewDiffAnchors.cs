// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Support;

/// <summary>Reads inserted line coordinates from normalized unified diffs.</summary>
public static class ReviewDiffAnchors
{
    public static IReadOnlyDictionary<string, HashSet<int>> BuildInsertedLineLookup(IReadOnlyList<ChangedFile> changedFiles)
    {
        var lookup = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            lookup[NormalizePath(changedFile.Path)] = ExtractInsertedNewLines(changedFile);
        }

        return lookup;
    }

    private static HashSet<int> ExtractInsertedNewLines(ChangedFile changedFile)
    {
        var insertedLines = new HashSet<int>();
        var diffLines = changedFile.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;

        foreach (var diffLine in diffLines)
        {
            ProcessUnifiedDiffLine(diffLine, insertedLines, ref currentNewLine, ref hasHunkHeader);
        }

        if (!hasHunkHeader && changedFile.ChangeType == ChangeType.Add)
        {
            var lineCount = CountLines(changedFile.FullContent);
            for (var lineNumber = 1; lineNumber <= lineCount; lineNumber++)
            {
                insertedLines.Add(lineNumber);
            }
        }

        return insertedLines;
    }

    // Classifies a single unified-diff line and updates the running new-file line cursor.
    private static void ProcessUnifiedDiffLine(
        string diffLine,
        HashSet<int> insertedLines,
        ref int currentNewLine,
        ref bool hasHunkHeader)
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

        switch (ReviewDiffProcessor.ClassifyHunkLine(diffLine))
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

    private static bool TryParseUnifiedDiffNewLineStart(string diffLine, out int newLineStart)
    {
        newLineStart = 0;

        var plusIndex = diffLine.IndexOf('+');
        if (plusIndex < 0)
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

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
    }

    public static string NormalizePath(string path)
    {
        return path.TrimStart('/');
    }
}
