// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Infrastructure.Features.Providers.Common;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class UnifiedDiffBuilderTests
{
    private const string FilePath = "src/Sample.cs";

    [Fact]
    public void Build_SingleLocalizedChange_EmitsOneHunkWithThreeContextLinesAndHeaders()
    {
        var oldContent = BuildLines(30);
        var newContent = WithLineChanged(oldContent, 15, "L15-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);
        var lines = SplitLines(diff);

        Assert.Contains("--- a/src/Sample.cs", lines);
        Assert.Contains("+++ b/src/Sample.cs", lines);
        Assert.Equal(1, HunkHeaderCount(diff));
        // 1 changed line + 3 context on each side = 7 lines on both old and new sides.
        Assert.Contains("@@ -12,7 +12,7 @@", lines);
        Assert.Contains("-L15", lines);
        Assert.Contains("+L15-CHANGED", lines);
        // Context lines are space-prefixed, not the old flat "  " double-space stream.
        Assert.Contains(" L12", lines);
        Assert.Contains(" L18", lines);
        // The unchanged top/bottom of the file is omitted (no full-file dump).
        Assert.DoesNotContain(" L01", lines);
        Assert.DoesNotContain(" L30", lines);
    }

    [Fact]
    public void Build_TwoChangesCloseTogether_MergeIntoOneHunk()
    {
        var oldContent = BuildLines(30);
        // Lines 15 and 18 are only two unchanged lines apart — well inside the 3-line context window.
        var newContent = WithLineChanged(WithLineChanged(oldContent, 15, "L15-CHANGED"), 18, "L18-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        Assert.Equal(1, HunkHeaderCount(diff));
    }

    [Fact]
    public void Build_TwoChangesFarApart_StayInSeparateHunks()
    {
        var oldContent = BuildLines(30);
        // Lines 5 and 25 have far more than six unchanged lines between them.
        var newContent = WithLineChanged(WithLineChanged(oldContent, 5, "L05-CHANGED"), 25, "L25-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        Assert.Equal(2, HunkHeaderCount(diff));
    }

    [Fact]
    public void Build_AddedFile_EmitsOnlyAddedPayload()
    {
        var diff = UnifiedDiffBuilder.Build(string.Empty, BuildLines(3), FilePath);
        var lines = SplitLines(diff);

        Assert.Equal(1, HunkHeaderCount(diff));
        Assert.Contains("@@ -1,0 +1,3 @@", lines);
        Assert.Equal(3, PayloadCount(lines, '+'));
        Assert.Equal(0, PayloadCount(lines, '-'));
    }

    [Fact]
    public void Build_DeletedFile_EmitsOnlyRemovedPayload()
    {
        var diff = UnifiedDiffBuilder.Build(BuildLines(3), string.Empty, FilePath);
        var lines = SplitLines(diff);

        Assert.Equal(1, HunkHeaderCount(diff));
        Assert.Contains("@@ -1,3 +1,0 @@", lines);
        Assert.Equal(3, PayloadCount(lines, '-'));
        Assert.Equal(0, PayloadCount(lines, '+'));
    }

    [Fact]
    public void Build_ChangeAtFileStart_ClipsLeadingContext()
    {
        var oldContent = BuildLines(10);
        var newContent = WithLineChanged(oldContent, 1, "L01-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        // No context exists before line 1, so the hunk spans only the change + 3 trailing context lines.
        Assert.Contains("@@ -1,4 +1,4 @@", SplitLines(diff));
    }

    [Fact]
    public void Build_ChangeAtFileEnd_ClipsTrailingContext()
    {
        var oldContent = BuildLines(10);
        var newContent = WithLineChanged(oldContent, 10, "L10-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        // No context exists after the last line, so the hunk is 3 leading context lines + the change.
        Assert.Contains("@@ -7,4 +7,4 @@", SplitLines(diff));
    }

    [Fact]
    public void Build_IdenticalContent_EmitsEmptyDiff()
    {
        var content = BuildLines(10);

        var diff = UnifiedDiffBuilder.Build(content, content, FilePath);

        Assert.Equal(string.Empty, diff);
    }

    [Fact]
    public void Build_AddedFileWithTrailingNewline_DoesNotEmitPhantomBlankLine()
    {
        // Real file content ends in a newline; the diff must still report exactly three added lines,
        // not a fourth phantom blank line.
        var diff = UnifiedDiffBuilder.Build(string.Empty, BuildLines(3) + "\n", FilePath);
        var lines = SplitLines(diff);

        Assert.Contains("@@ -1,0 +1,3 @@", lines);
        Assert.Equal(3, PayloadCount(lines, '+'));
        Assert.DoesNotContain("+", lines); // no bare "+" (empty added line)
    }

    [Fact]
    public void Build_EditWithTrailingNewline_DoesNotEmitPhantomContextLine()
    {
        var oldContent = BuildLines(5) + "\n";
        var newContent = WithLineChanged(BuildLines(5), 3, "L03-CHANGED") + "\n";

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);
        var lines = SplitLines(diff);

        Assert.Equal(1, PayloadCount(lines, '+'));
        Assert.Equal(1, PayloadCount(lines, '-'));
        // The trailing newline must not produce a dangling empty context line at the end of the hunk.
        Assert.DoesNotContain(" ", lines); // no bare single-space (empty context line)
        Assert.Equal(string.Empty, lines[^1]); // output ends with a single newline, nothing after it
    }

    [Fact]
    public void Build_ChangesSixUnchangedLinesApart_MergeIntoOneHunk()
    {
        var oldContent = BuildLines(30);
        // Lines 10 and 17 leave six unchanged lines (11-16) between them: the 3-line context windows
        // touch, so git (and the chosen builder) merge them into one hunk.
        var newContent = WithLineChanged(WithLineChanged(oldContent, 10, "L10-CHANGED"), 17, "L17-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        Assert.Equal(1, HunkHeaderCount(diff));
    }

    [Fact]
    public void Build_ChangesSevenUnchangedLinesApart_StayInSeparateHunks()
    {
        var oldContent = BuildLines(30);
        // Lines 10 and 18 leave seven unchanged lines (11-17) between them: one more than the merge
        // window, so the hunks stay separate.
        var newContent = WithLineChanged(WithLineChanged(oldContent, 10, "L10-CHANGED"), 18, "L18-CHANGED");

        var diff = UnifiedDiffBuilder.Build(oldContent, newContent, FilePath);

        Assert.Equal(2, HunkHeaderCount(diff));
    }

    private static string BuildLines(int count)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            if (i > 1)
            {
                builder.Append('\n');
            }

            builder.Append($"L{i:D2}");
        }

        return builder.ToString();
    }

    private static string WithLineChanged(string content, int oneBasedLine, string replacement)
    {
        var lines = content.Split('\n');
        lines[oneBasedLine - 1] = replacement;
        return string.Join('\n', lines);
    }

    private static string[] SplitLines(string diff)
    {
        return diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static int HunkHeaderCount(string diff)
    {
        return SplitLines(diff).Count(line => line.StartsWith("@@", StringComparison.Ordinal));
    }

    // Counts payload lines for a given sign, excluding the "---"/"+++" file headers.
    private static int PayloadCount(string[] lines, char sign)
    {
        var header = new string(sign, 3);
        return lines.Count(line =>
            line.Length > 0 &&
            line[0] == sign &&
            !line.StartsWith(header, StringComparison.Ordinal));
    }
}
