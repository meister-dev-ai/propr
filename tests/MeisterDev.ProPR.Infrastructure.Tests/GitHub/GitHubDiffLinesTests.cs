// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubDiffLinesTests
{
    // The patch GitHub returns when line 30 of a sixty-line file changes and line 45 is removed, with GitHub's
    // three context lines around each change.
    internal const string TwoHunkPatch =
        "@@ -27,7 +27,7 @@ line 26\n line 27\n line 28\n line 29\n-line 30\n+line 30 changed\n line 31\n line 32\n line 33\n"
        + "@@ -42,7 +42,6 @@ line 41\n line 42\n line 43\n line 44\n-line 45\n line 46\n line 47\n line 48";

    [Fact]
    public void ParseRightLines_ReturnsTheAddedAndContextLinesOfEachHunk()
    {
        var lines = GitHubDiffLines.ParseRightLines(TwoHunkPatch);

        // The removed line 45 shifts the following lines up by one on the right side.
        Assert.Equal([27, 28, 29, 30, 31, 32, 33, 42, 43, 44, 45, 46, 47], lines.Order());
    }

    [Fact]
    public void ParseRightLines_CountsAnEmptyContextLineWithoutItsLeadingSpace()
    {
        var lines = GitHubDiffLines.ParseRightLines("@@ -1,3 +1,4 @@\n first\n\n+added\n last");

        Assert.Equal([1, 2, 3, 4], lines.Order());
    }

    [Fact]
    public void ParseRightLines_IgnoresTheNoNewlineMarkerAndReadsHeadersWithoutCounts()
    {
        var lines = GitHubDiffLines.ParseRightLines("@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file");

        Assert.Equal([1], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseRightLines_WithoutAPatch_ReturnsNoLines(string? patch)
    {
        Assert.Empty(GitHubDiffLines.ParseRightLines(patch));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(27, true)]
    [InlineData(33, true)]
    [InlineData(34, false)]
    [InlineData(10, false)]
    [InlineData(45, true)]
    [InlineData(48, false)]
    public void Accepts_FollowsTheHunksOfAListedFile(int line, bool accepted)
    {
        var diff = GitHubDiffLines.FromFiles([("review-line-check.txt", TwoHunkPatch)], complete: true);

        Assert.Equal(accepted, diff.Accepts("/review-line-check.txt", line));
    }

    [Fact]
    public void Accepts_RefusesEveryLineOfAListedFileWithoutAPatch()
    {
        var diff = GitHubDiffLines.FromFiles([("assets/logo.png", null)], complete: true);

        Assert.False(diff.Accepts("assets/logo.png", 1));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Accepts_DecidesAFileMissingFromTheListingByCompleteness(bool complete, bool accepted)
    {
        var diff = GitHubDiffLines.FromFiles([("review-line-check.txt", TwoHunkPatch)], complete);

        Assert.Equal(accepted, diff.Accepts("src/Unchanged.cs", 12));
    }

    [Fact]
    public void None_AcceptsNoLine()
    {
        Assert.False(GitHubDiffLines.None.Accepts("review-line-check.txt", 30));
    }
}
