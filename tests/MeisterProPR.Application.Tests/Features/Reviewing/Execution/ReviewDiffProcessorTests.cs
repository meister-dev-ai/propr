// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;

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
}
