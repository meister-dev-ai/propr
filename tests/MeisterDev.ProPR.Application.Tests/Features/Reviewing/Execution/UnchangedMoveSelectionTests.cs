// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     A file that a pull request only moves or renames has no changed line, so ProPR sets it aside instead of
///     reviewing it. ProPR decides from the change type, the original path and the diff that every provider delivers.
/// </summary>
public sealed class UnchangedMoveSelectionTests
{
    // Git reports a rename at 100 % similarity with a header and no hunk.
    private const string GitPureRenameDiff =
        "diff --git a/src/Old.cs b/src/New.cs\nsimilarity index 100%\nrename from src/Old.cs\nrename to src/New.cs\n";

    private const string EditDiff = "@@ -1,2 +1,2 @@\n context\n-old\n+new\n";

    private static readonly IReadOnlyDictionary<string, ReviewFileResult> NoResults = new Dictionary<string, ReviewFileResult>();

    // The source of a renamed file confirmed equal content unless a test says otherwise.
    private static ChangedFile Renamed(string path, string originalPath, string diff, bool isBinary = false, bool contentUnchanged = true)
    {
        return new ChangedFile(path, ChangeType.Rename, "content", diff, isBinary, originalPath, contentUnchanged);
    }

    [Fact]
    public void SelectFilesForReview_SetsAsideAFileThatIsOnlyMoved_AndReviewsTheOthers()
    {
        IReadOnlyList<ChangedFile> changedFiles =
        [
            Renamed("src/New.cs", "src/Old.cs", GitPureRenameDiff),
            Renamed("src/Changed.cs", "src/Before.cs", EditDiff),
            new("src/Edited.cs", ChangeType.Edit, "content", EditDiff),
        ];

        var selection = ReviewFileSelectionService.SelectFilesForReview(changedFiles, NoResults, ReviewExclusionRules.Empty);

        Assert.Equal(["src/New.cs"], selection.UnchangedMoveFiles.Select(file => file.Path));
        Assert.Equal(["src/Changed.cs", "src/Edited.cs"], selection.FilesToReview.Select(file => file.Path));
        Assert.Empty(selection.ExcludedFiles);
    }

    // A configured exclusion pattern is the operator's own decision, so it takes precedence and the file keeps the
    // pattern as its reason.
    [Fact]
    public void SelectFilesForReview_CountsAMovedFileThatAPatternMatchesAsExcluded()
    {
        IReadOnlyList<ChangedFile> changedFiles = [Renamed("generated/New.cs", "generated/Old.cs", string.Empty)];

        var selection = ReviewFileSelectionService.SelectFilesForReview(changedFiles, NoResults, ReviewExclusionRules.FromPatterns(["generated/**"]));

        Assert.Equal(["generated/New.cs"], selection.ExcludedFiles.Select(file => file.Path));
        Assert.Empty(selection.UnchangedMoveFiles);
    }

    // A moved file that an earlier attempt of the job already recorded is complete, so a retry does not record it
    // again.
    [Fact]
    public void SelectFilesForReview_LeavesOutAMovedFileThatIsAlreadyRecorded()
    {
        var recorded = new ReviewFileResult(Guid.NewGuid(), "src/New.cs");
        recorded.MarkExcluded("Moved from src/Old.cs without content changes");
        IReadOnlyList<ChangedFile> changedFiles = [Renamed("src/New.cs", "src/Old.cs", string.Empty)];

        var selection = ReviewFileSelectionService.SelectFilesForReview(
            changedFiles,
            new Dictionary<string, ReviewFileResult> { ["src/New.cs"] = recorded },
            ReviewExclusionRules.Empty);

        Assert.Empty(selection.UnchangedMoveFiles);
        Assert.Empty(selection.FilesToReview);
    }

    [Theory]
    [InlineData("")]
    [InlineData(GitPureRenameDiff)]
    [InlineData("--- a/src/New.cs\n+++ b/src/New.cs\n")]
    [InlineData("diff --git a/src/Old.sh b/src/New.sh\nold mode 100644\nnew mode 100755\nsimilarity index 100%\n")]
    public void IsUnchangedMove_HoldsForARenameWhoseDiffChangesNoLine(string diff)
    {
        Assert.True(ReviewFileSelectionService.IsUnchangedMove(Renamed("src/New.cs", "src/Old.cs", diff)));
    }

    [Theory]
    [InlineData(EditDiff)]
    [InlineData("@@ -1,1 +0,0 @@\n--- removed SQL comment\n")]
    [InlineData("+added without a hunk header\n")]
    [InlineData("diff --git a/logo.dat b/logo2.dat\nsimilarity index 90%\nBinary files a/logo.dat and b/logo2.dat differ\n")]
    public void IsUnchangedMove_DoesNotHoldForARenameWithAChangedLine(string diff)
    {
        Assert.False(ReviewFileSelectionService.IsUnchangedMove(Renamed("src/New.cs", "src/Old.cs", diff)));
    }

    // Without the original path ProPR cannot tell a move from a new file, and a binary file has an empty diff
    // whether its content changed or not.
    [Fact]
    public void IsUnchangedMove_DoesNotHoldWithoutAnOriginalPath_ForABinaryFile_OrForAnEdit()
    {
        Assert.False(
            ReviewFileSelectionService.IsUnchangedMove(new ChangedFile("src/New.cs", ChangeType.Rename, "content", string.Empty, contentUnchanged: true)));
        Assert.False(ReviewFileSelectionService.IsUnchangedMove(Renamed("assets/logo.png", "img/logo.png", string.Empty, isBinary: true)));
        Assert.False(
            ReviewFileSelectionService.IsUnchangedMove(new ChangedFile("src/Same.cs", ChangeType.Edit, "content", string.Empty, contentUnchanged: true)));
    }

    // A failed read of either side leaves empty content, and a diff built from it has no changed line. Only the
    // confirmation of the source that the content is the same makes a rename an unchanged move.
    [Fact]
    public void IsUnchangedMove_DoesNotHold_WhenTheSourceDidNotConfirmTheContent()
    {
        var unconfirmed = Renamed("src/New.cs", "src/Old.cs", string.Empty, contentUnchanged: false);

        Assert.False(ReviewFileSelectionService.IsUnchangedMove(unconfirmed));
        Assert.Equal(
            ["src/New.cs"],
            ReviewFileSelectionService.SelectFilesForReview([unconfirmed], NoResults, ReviewExclusionRules.Empty).FilesToReview.Select(file => file.Path));
    }

    [Fact]
    public void DescribeUnchangedMove_NamesThePathTheFileWasMovedFrom()
    {
        var reason = ReviewFileSelectionService.DescribeUnchangedMove(Renamed("src/New.cs", "src/Old.cs", string.Empty));

        Assert.Equal("Moved from src/Old.cs without content changes", reason);
    }
}
