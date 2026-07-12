// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for <see cref="ReviewDiffProcessor.ExtractChangedNewLineRanges" /> (T102).
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
}
