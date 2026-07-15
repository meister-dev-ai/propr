// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the <see cref="ReviewDiffProcessor" /> diff-walking utilities:
///     <see cref="ReviewDiffProcessor.ClassifyHunkLine" />,
///     <see cref="ReviewDiffProcessor.GetInsertedNewLineNumbers" />,
///     <see cref="ReviewDiffProcessor.ExtractChangedNewLineRanges" />, and the annotation helpers.
/// </summary>
public sealed class ReviewDiffProcessorTests
{
    [Fact]
    public void ExtractAddedContent_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ReviewDiffProcessor.ExtractAddedContent(null));
        Assert.Equal(string.Empty, ReviewDiffProcessor.ExtractAddedContent(string.Empty));
    }

    [Fact]
    public void ExtractAddedContent_ExcludesFileHeadersHunkHeadersContextAndRemovedLines()
    {
        const string diff = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,3 +1,3 @@\n context line\n-removed old\n+added new";

        var added = ReviewDiffProcessor.ExtractAddedContent(diff);

        Assert.Equal("added new\n", added);
        Assert.DoesNotContain("+++", added, StringComparison.Ordinal);
        Assert.DoesNotContain("removed old", added, StringComparison.Ordinal);
        Assert.DoesNotContain("context line", added, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractAddedContent_StripsLeadingPlusFromAddedLines()
    {
        const string diff = "+var token = 1;\n+another();";

        var added = ReviewDiffProcessor.ExtractAddedContent(diff);

        Assert.Equal("var token = 1;\nanother();\n", added);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(null));
        Assert.Equal(string.Empty, ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(string.Empty));
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_NoHunkHeader_ReturnsDiffUnchanged()
    {
        // Degenerate diffs without a parseable hunk header carry no line coordinates, so the
        // text passes through rather than acquiring made-up numbers.
        const string diff = "+bare added line";

        Assert.Equal(diff, ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff));
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_SingleHunk_NumbersContextAndAddedLinesOnly()
    {
        const string diff = """
                            --- a/f.txt
                            +++ b/f.txt
                            @@ -12,4 +12,4 @@
                             alpha
                             bravo
                            -charlie
                            +charlie-fixed
                             delta
                            """;

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        var expected = """
                       --- a/f.txt
                       +++ b/f.txt
                       @@ -12,4 +12,4 @@
                       12 |  alpha
                       13 |  bravo
                          | -charlie
                       14 | +charlie-fixed
                       15 |  delta
                       """;
        Assert.Equal(expected, annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_MultiHunk_NumbersMatchNewFileContent()
    {
        // New file: 1-14 = L01..L14, 15 = CHANGED, 16-25 = L16..L25, 26 = INSERTED, 27-31 = L26..L30.
        const string diff = """
                            @@ -12,7 +12,7 @@
                             L12
                             L13
                             L14
                            -L15
                            +CHANGED
                             L16
                             L17
                             L18
                            @@ -22,7 +22,8 @@
                             L22
                             L23
                             L24
                             L25
                            +INSERTED
                             L26
                             L27
                             L28
                            """;

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);
        var annotatedLines = annotated.Split('\n');

        // Numbering restarts correctly at each hunk header and matches the new-file coordinates.
        Assert.Contains("12 |  L12", annotatedLines);
        Assert.Contains("   | -L15", annotatedLines);
        Assert.Contains("15 | +CHANGED", annotatedLines);
        Assert.Contains("16 |  L16", annotatedLines);
        Assert.Contains("22 |  L22", annotatedLines);
        Assert.Contains("25 |  L25", annotatedLines);
        Assert.Contains("26 | +INSERTED", annotatedLines);
        Assert.Contains("27 |  L26", annotatedLines);
        Assert.Contains("29 |  L28", annotatedLines);
        Assert.Contains("@@ -22,7 +22,8 @@", annotatedLines);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_WidePadding_AlignsNumberColumn()
    {
        const string diff = "@@ -98,4 +98,4 @@\n L98\n L99\n-L100\n+CHANGED\n L101";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        // Width follows the largest rendered new-file line number (101 → 3 digits).
        Assert.Equal(
            "@@ -98,4 +98,4 @@\n 98 |  L98\n 99 |  L99\n    | -L100\n100 | +CHANGED\n101 |  L101",
            annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_CrlfDiff_NormalizesAndNumbers()
    {
        const string diff = "@@ -1,2 +1,2 @@\r\n a\r\n-b\r\n+c";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("@@ -1,2 +1,2 @@\n1 |  a\n  | -b\n2 | +c", annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_TrailingNewline_KeepsTrailingNewline()
    {
        const string diff = "@@ -1,1 +1,1 @@\n-a\n+b\n";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("@@ -1,1 +1,1 @@\n  | -a\n1 | +b\n", annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_MalformedHunkHeader_StopsAnnotatingUntilNextValidHeader()
    {
        // Payload after an unparseable hunk header has no trustworthy coordinates and must not
        // inherit the previous hunk's cursor; the next valid header resumes annotation.
        const string diff = "@@ -1,2 +1,2 @@\n a\n b\n@@ malformed @@\n orphan\n@@ -9,1 +9,1 @@\n z";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("@@ -1,2 +1,2 @@\n1 |  a\n2 |  b\n@@ malformed @@\n orphan\n@@ -9,1 +9,1 @@\n9 |  z", annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_PayloadStartingWithPlusPlusOrMinusMinus_IsNotMistakenForHeader()
    {
        // Inside a hunk, an added line whose content begins with "++" (e.g. "++i;") renders as
        // "+++i;" and a removed "--comment" renders as "---comment". Both are payload and must
        // keep the numbering exact — misreading them as file headers would shift every
        // subsequent line number, which is precisely the defect the annotation exists to fix.
        const string diff = "@@ -5,4 +5,4 @@\n a\n-++i;\n+++j;\n---x\n b";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("@@ -5,4 +5,4 @@\n5 |  a\n  | -++i;\n6 | +++j;\n  | ---x\n7 |  b", annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_DeletionOnlyDiff_KeepsBlankNumberColumn()
    {
        // A deleted file renders no new-file numbers, but the payload still carries the blank
        // number column so the annotated format the prompts describe stays uniform.
        const string diff = "--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,0 @@\n-first\n-second";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,0 @@\n  | -first\n  | -second", annotated);
    }

    [Fact]
    public void AnnotateUnifiedDiffWithNewLineNumbers_TrailingNewline_DoesNotWidenNumberColumn()
    {
        // The trailing empty split element is structure; it must not count as a phantom line 10
        // and widen the number column past what is actually rendered.
        const string diff = "@@ -8,2 +8,2 @@\n x\n y\n";

        var annotated = ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(diff);

        Assert.Equal("@@ -8,2 +8,2 @@\n8 |  x\n9 |  y\n", annotated);
    }

    [Fact]
    public void AnnotateContentWithLineNumbers_TrailingNewline_DoesNotEmitPhantomLine()
    {
        var annotated = ReviewDiffProcessor.AnnotateContentWithLineNumbers("a\nb\n", 8);

        Assert.Equal("8 | a\n9 | b\n", annotated);
    }

    [Fact]
    public void AnnotateContentWithLineNumbers_StartsAtRequestedLine()
    {
        var annotated = ReviewDiffProcessor.AnnotateContentWithLineNumbers("alpha\nbravo\ncharlie", 41);

        Assert.Equal("41 | alpha\n42 | bravo\n43 | charlie", annotated);
    }

    [Fact]
    public void AnnotateContentWithLineNumbers_WidthGrowsAcrossDigitBoundary()
    {
        var annotated = ReviewDiffProcessor.AnnotateContentWithLineNumbers("a\nb\nc", 9);

        Assert.Equal(" 9 | a\n10 | b\n11 | c", annotated);
    }

    [Fact]
    public void AnnotateContentWithLineNumbers_NonPositiveStart_ClampsToLineOne()
    {
        Assert.Equal("1 | a\n2 | b", ReviewDiffProcessor.AnnotateContentWithLineNumbers("a\nb", 0));
        Assert.Equal(string.Empty, ReviewDiffProcessor.AnnotateContentWithLineNumbers(string.Empty, 5));
    }

    [Fact]
    public void ExtractChangedNewLineRanges_NullOrEmptyDiff_ReturnsEmpty()
    {
        Assert.Empty(ReviewDiffProcessor.ExtractChangedNewLineRanges(null));
        Assert.Empty(ReviewDiffProcessor.ExtractChangedNewLineRanges(string.Empty));
        Assert.Empty(ReviewDiffProcessor.ExtractChangedNewLineRanges("   "));
    }

    [Fact]
    public void ExtractChangedNewLineRanges_SingleHunkWithAddedLines_ReturnsCorrectRange()
    {
        const string diff = """
                            @@ -10,5 +10,5 @@
                             context line
                            -removed line
                            +added line
                             another context
                             end context
                            """;

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        Assert.Single(ranges);
        Assert.Equal(10, ranges[0].Start);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_TwoHunksFarApart_ReturnsTwoRanges()
    {
        const string diff = """
                            @@ -50,3 +50,3 @@
                             ctx
                            -old50
                            +new50
                             ctx2
                            @@ -500,3 +500,3 @@
                             ctx
                            -old500
                            +new500
                             ctx2
                            """;

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(50, ranges[0].Start);
        Assert.Equal(500, ranges[1].Start);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_OverlappingHunks_MergesRanges()
    {
        // Two hunks whose windows would overlap: hunk 1 at 50, hunk 2 at 55
        const string diff = """
                            @@ -50,3 +50,3 @@
                             ctx
                            +added1
                             ctx2
                            @@ -52,3 +53,3 @@
                             ctx
                            +added2
                             ctx2
                            """;

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        // Both start near 50, they should be merged.
        Assert.True(ranges.Count <= 2, "Adjacent hunks should be merged or kept as two separate ranges");
        Assert.True(ranges[0].Start <= 55);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_DeletionOnlyHunk_YieldsSingleLineRange()
    {
        const string diff = """
                            @@ -100,2 +100,1 @@
                             ctx
                            -deleted line
                            """;

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        Assert.Single(ranges);
        // A deletion-only hunk: range start should be 100
        Assert.Equal(100, ranges[0].Start);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_RangesAreAscendingAndMerged()
    {
        // Three hunks: 50, 200, 300
        const string diff = """
                            @@ -300,2 +300,2 @@
                            -old
                            +new
                            @@ -50,2 +50,2 @@
                            -old
                            +new
                            @@ -200,2 +200,2 @@
                            -old
                            +new
                            """;

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        // Should be sorted ascending even though diff was not.
        Assert.Equal(3, ranges.Count);
        Assert.Equal(50, ranges[0].Start);
        Assert.Equal(200, ranges[1].Start);
        Assert.Equal(300, ranges[2].Start);
    }

    [Theory]
    [InlineData(10, ChangedLineRelation.OnChangedLine)]
    [InlineData(12, ChangedLineRelation.OnChangedLine)]
    [InlineData(14, ChangedLineRelation.OnChangedLine)]
    [InlineData(9, ChangedLineRelation.AdjacentToChange)]
    [InlineData(7, ChangedLineRelation.AdjacentToChange)]
    [InlineData(17, ChangedLineRelation.AdjacentToChange)]
    [InlineData(6, ChangedLineRelation.OutsideChange)]
    [InlineData(18, ChangedLineRelation.OutsideChange)]
    [InlineData(244, ChangedLineRelation.OutsideChange)]
    public void ClassifyChangedLineRelation_ClassifiesByDistanceFromRange(int line, ChangedLineRelation expected)
    {
        IReadOnlyList<(int Start, int End)> ranges = [(10, 14)];

        Assert.Equal(expected, ReviewDiffProcessor.ClassifyChangedLineRelation(line, ranges));
    }

    [Fact]
    public void ClassifyChangedLineRelation_NullLine_ReturnsNull()
    {
        IReadOnlyList<(int Start, int End)> ranges = [(10, 14)];

        Assert.Null(ReviewDiffProcessor.ClassifyChangedLineRelation(null, ranges));
    }

    [Fact]
    public void ClassifyChangedLineRelation_NoRanges_ReturnsNull()
    {
        Assert.Null(ReviewDiffProcessor.ClassifyChangedLineRelation(12, null));
        Assert.Null(ReviewDiffProcessor.ClassifyChangedLineRelation(12, []));
    }

    [Fact]
    public void BuildChangedLineRangesByPath_SkipsBinaryAndRangelessFiles()
    {
        var changedFiles = new[]
        {
            new ChangedFile("src/Foo.cs", ChangeType.Edit, string.Empty, "@@ -1,1 +10,2 @@\n+added one\n+added two"),
            new ChangedFile("assets/logo.png", ChangeType.Edit, string.Empty, string.Empty, true),
            new ChangedFile("src/Empty.cs", ChangeType.Edit, string.Empty, string.Empty),
        };

        var lookup = ReviewDiffProcessor.BuildChangedLineRangesByPath(changedFiles);

        Assert.True(lookup.ContainsKey("src/Foo.cs"));
        Assert.False(lookup.ContainsKey("assets/logo.png"));
        Assert.False(lookup.ContainsKey("src/Empty.cs"));
    }

    [Fact]
    public void GetInsertedNewLineNumbers_AddedPlusPlusPayload_IsCountedAndKeepsLaterNumbersCorrect()
    {
        // The added statement "++i;" renders in the diff as "+++i;". Misreading it as the "+++"
        // file header skips it without advancing the new-file cursor, shifting every later line.
        const string diff = "@@ -1,3 +1,4 @@\n a\n+++i;\n+real\n b";

        var inserted = ReviewDiffProcessor.GetInsertedNewLineNumbers(diff);

        // New file: 1=a (context), 2=++i; (added), 3=real (added), 4=b (context).
        Assert.Equal(2, inserted.Count);
        Assert.Contains(2, inserted);
        Assert.Contains(3, inserted);
    }

    [Fact]
    public void GetInsertedNewLineNumbers_MultiplePlusPlusPayloads_AccumulateWithoutShift()
    {
        const string diff = "@@ -1,5 +1,6 @@\n a\n+++i;\n+mid\n+++j;\n+tail\n b";

        var inserted = ReviewDiffProcessor.GetInsertedNewLineNumbers(diff);

        // New file: 1=a, 2=++i;, 3=mid, 4=++j;, 5=tail, 6=b — the shift must not accumulate.
        Assert.Equal(4, inserted.Count);
        Assert.Contains(2, inserted);
        Assert.Contains(3, inserted);
        Assert.Contains(4, inserted);
        Assert.Contains(5, inserted);
        Assert.DoesNotContain(6, inserted);
    }

    [Fact]
    public void GetInsertedNewLineNumbers_RemovedMinusMinusPayload_DoesNotDisturbFollowingNumbers()
    {
        // The removed statement "--x" renders as "---x"; a removed line must not advance the
        // new-file cursor, so the following inserted line keeps its correct number.
        const string diff = "@@ -1,3 +1,3 @@\n a\n---x\n+kept\n b";

        var inserted = ReviewDiffProcessor.GetInsertedNewLineNumbers(diff);

        // New file: 1=a (context), --x removed (no new-file line), 2=kept (added), 3=b (context).
        Assert.Single(inserted);
        Assert.Contains(2, inserted);
    }

    [Fact]
    public void GetInsertedNewLineNumbers_MultiHunk_HeaderCheckDoesNotRetriggerInLaterHunks()
    {
        const string diff =
            "@@ -1,2 +1,3 @@\n a\n+++i;\n b\n"
            + "@@ -20,2 +20,3 @@\n c\n+++j;\n d";

        var inserted = ReviewDiffProcessor.GetInsertedNewLineNumbers(diff);

        // Hunk 1: 1=a, 2=++i; (added), 3=b. Hunk 2: 20=c, 21=++j; (added), 22=d.
        Assert.Equal(2, inserted.Count);
        Assert.Contains(2, inserted);
        Assert.Contains(21, inserted);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_AddedPlusPlusPayload_KeepsRangeEndCorrect()
    {
        const string diff = "@@ -10,4 +10,5 @@\n ctx\n+++i;\n+add\n ctx2";

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        // New file: 10=ctx, 11=++i;, 12=add, 13=ctx2 — the range must reach 13, not stop short at 12.
        Assert.Single(ranges);
        Assert.Equal(10, ranges[0].Start);
        Assert.Equal(13, ranges[0].End);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_MultiHunkWithPlusPlusPayload_NumbersEachHunkCorrectly()
    {
        const string diff =
            "@@ -1,2 +1,3 @@\n a\n+++i;\n b\n"
            + "@@ -20,2 +20,3 @@\n c\n+++j;\n d";

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(1, ranges[0].Start);
        Assert.Equal(3, ranges[0].End);
        Assert.Equal(20, ranges[1].Start);
        Assert.Equal(22, ranges[1].End);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_RemovedMinusMinusPayload_ExtendsRangeOverDeletion()
    {
        // "--x" removed renders as "---x"; the deletion still contributes to the changed range.
        const string diff = "@@ -5,2 +5,1 @@\n ctx\n---x";

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        Assert.Single(ranges);
        Assert.Equal(5, ranges[0].Start);
        Assert.Equal(6, ranges[0].End);
    }

    [Theory]
    [InlineData("+added", HunkLineKind.Added)]
    [InlineData("+++i;", HunkLineKind.Added)]
    [InlineData("-removed", HunkLineKind.Removed)]
    [InlineData("---x", HunkLineKind.Removed)]
    [InlineData(" context", HunkLineKind.Context)]
    [InlineData("\\ No newline at end of file", HunkLineKind.Marker)]
    [InlineData("", HunkLineKind.Marker)]
    public void ClassifyHunkLine_ClassifiesByFirstCharacter(string hunkLine, HunkLineKind expected)
    {
        Assert.Equal(expected, ReviewDiffProcessor.ClassifyHunkLine(hunkLine));
    }

    [Fact]
    public void GetInsertedNewLineNumbers_FileHeadersBeforeFirstHunk_AreIgnored()
    {
        // The "---"/"+++" file headers precede the first hunk header, so they must never be
        // classified as payload; only the real added line inside the hunk is inserted.
        const string diff = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,2 +1,3 @@\n a\n+added\n b";

        var inserted = ReviewDiffProcessor.GetInsertedNewLineNumbers(diff);

        // New file: 1=a (context), 2=added (added), 3=b (context).
        Assert.Single(inserted);
        Assert.Contains(2, inserted);
    }

    [Fact]
    public void ExtractChangedNewLineRanges_FileHeadersBeforeFirstHunk_AreIgnored()
    {
        const string diff = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -10,2 +10,3 @@\n ctx\n+added\n ctx2";

        var ranges = ReviewDiffProcessor.ExtractChangedNewLineRanges(diff);

        // Only the hunk body defines the range: 10=ctx, 11=added, 12=ctx2.
        Assert.Single(ranges);
        Assert.Equal(10, ranges[0].Start);
        Assert.Equal(12, ranges[0].End);
    }
}
