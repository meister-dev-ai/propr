// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing;

public sealed class ReviewingExecutionUtilitiesTests
{
    [Fact]
    public void ReviewDiffProcessor_CountChangedLines_IgnoresUnifiedDiffHeaders()
    {
        var diff = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,2 +1,3 @@\n+added\n context\n-removed";

        var count = ReviewDiffProcessor.CountChangedLines(diff);

        Assert.Equal(2, count);
    }

    [Fact]
    public void ReviewDiffProcessor_ClassifyTier_UsesChangedLineThresholds()
    {
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", string.Join('\n', Enumerable.Repeat("+line", 31)));

        var tier = ReviewDiffProcessor.ClassifyTier(file);

        Assert.Equal(FileComplexityTier.Medium, tier);
    }

    [Fact]
    public void ReviewDiffProcessor_BuildInsertedLineLookup_TracksInsertedNewLineNumbersByNormalizedPath()
    {
        IReadOnlyList<ChangedFile> changedFiles =
        [
            new(
                "/src/Foo.cs",
                ChangeType.Edit,
                "content",
                "@@ -1,2 +4,3 @@\n unchanged\n+first\n+second\n-third"),
        ];

        var lookup = ReviewDiffProcessor.BuildInsertedLineLookup(changedFiles);

        Assert.True(lookup.TryGetValue("src/Foo.cs", out var insertedLines));
        Assert.Equal([5, 6], insertedLines!.OrderBy(x => x));
    }

    [Fact]
    public void ReviewFileSelectionService_SelectsRemainingFilesAndExcludedFiles()
    {
        IReadOnlyList<ChangedFile> changedFiles =
        [
            new("src/Keep.cs", ChangeType.Edit, "content", "+line"),
            new("src/Skip.cs", ChangeType.Edit, "content", "+line"),
            new("src/Done.cs", ChangeType.Edit, "content", "+line"),
        ];
        var existingResults = new Dictionary<string, ReviewFileResult>
        {
            ["src/Done.cs"] = CreateCompletedFileResult("src/Done.cs"),
        };
        var exclusions = ReviewExclusionRules.FromPatterns(["src/Skip.cs"]);

        var selection = ReviewFileSelectionService.SelectFilesForReview(changedFiles, existingResults, exclusions);

        Assert.Equal(["src/Keep.cs"], selection.FilesToReview.Select(file => file.Path));
        Assert.Equal(["src/Skip.cs"], selection.ExcludedFiles.Select(file => file.Path));
    }

    private static ReviewFileResult CreateCompletedFileResult(string path)
    {
        var fileResult = new ReviewFileResult(Guid.NewGuid(), path);
        fileResult.MarkCompleted("summary", []);
        return fileResult;
    }
}
