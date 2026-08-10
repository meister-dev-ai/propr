// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewPassProvenanceTests
{
    private static readonly Guid ModelA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ModelB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ChangedFile File(string path)
    {
        return new ChangedFile(path, ChangeType.Edit, string.Empty, string.Empty);
    }

    private static ReviewFileResult CompletedResult(string path, IReadOnlyList<string>? passKeys)
    {
        var result = new ReviewFileResult(Guid.NewGuid(), path);
        result.MarkCompleted($"summary for {path}", [], passKeys);
        return result;
    }

    [Fact]
    public void PassKeys_AreOrderedAndDistinguishTheThingsThatChangeAReview()
    {
        var lensed = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA, Lens: "security")]);
        var plain = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA)]);
        var otherModel = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelB)]);

        Assert.NotEqual(plain, lensed);
        Assert.NotEqual(plain, otherModel);
    }

    // The list is ordered and a pass's ordinal is how its trace is identified, so reordering it is a real
    // configuration change rather than a cosmetic one.
    [Fact]
    public void PassKeys_ChangeWhenTheListIsReordered()
    {
        var forward = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA), new ReviewPassSpec(ModelB)]);
        var reversed = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelB), new ReviewPassSpec(ModelA)]);

        Assert.NotEqual(forward, reversed);
    }

    // Results written before provenance existed, and results carried forward from another revision, have no
    // recorded passes. Re-reviewing all of them would charge an installation for its backlog on upgrade.
    [Fact]
    public void AResultWithNoRecordedPasses_IsTakenAtFaceValue()
    {
        Assert.True(ReviewPassSignature.Matches(null, ["1:model:-:-:None"]));
        Assert.True(ReviewPassSignature.Matches([], ["1:model:-:-:None"]));
    }

    [Fact]
    public void SelectFilesForReview_SkipsACompletedFileWhosePassesStillMatch()
    {
        var passes = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA)]);
        var existing = new Dictionary<string, ReviewFileResult>
        {
            ["src/a.cs"] = CompletedResult("src/a.cs", passes),
        };

        var selection = ReviewFileSelectionService.SelectFilesForReview(
            [File("src/a.cs"), File("src/b.cs")],
            existing,
            ReviewExclusionRules.Empty,
            passes);

        Assert.Equal(["src/b.cs"], selection.FilesToReview.Select(f => f.Path));
    }

    // The gap this closes: a file finished under one pass list looked identical to the same file finished
    // under another, so a resume adopted work the client's configuration no longer asks for.
    [Fact]
    public void SelectFilesForReview_ReviewsAgain_WhenThePassListHasChangedSince()
    {
        var existing = new Dictionary<string, ReviewFileResult>
        {
            ["src/a.cs"] = CompletedResult("src/a.cs", ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA)])),
        };

        var selection = ReviewFileSelectionService.SelectFilesForReview(
            [File("src/a.cs")],
            existing,
            ReviewExclusionRules.Empty,
            ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA), new ReviewPassSpec(ModelB, Lens: "security")]));

        Assert.Equal(["src/a.cs"], selection.FilesToReview.Select(f => f.Path));
    }

    [Fact]
    public void SelectFilesForReview_SkipsAnUnannotatedCompletedFile()
    {
        var existing = new Dictionary<string, ReviewFileResult>
        {
            ["src/a.cs"] = CompletedResult("src/a.cs", null),
        };

        var selection = ReviewFileSelectionService.SelectFilesForReview(
            [File("src/a.cs")],
            existing,
            ReviewExclusionRules.Empty,
            ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA)]));

        Assert.Empty(selection.FilesToReview);
    }

    // A resumed result stands in for the work the earlier attempt did, so it has to carry that attempt's
    // provenance too. Losing it would make every resumed file look unannotated and permanently skippable.
    [Fact]
    public void AResumedResult_KeepsTheProvenanceOfTheWorkItAdopts()
    {
        var passes = ReviewPassSignature.ForPasses([new ReviewPassSpec(ModelA, Lens: "security")]);
        var prior = CompletedResult("src/a.cs", passes);

        var resumed = ReviewFileResult.CreateResumed(Guid.NewGuid(), prior);

        Assert.Equal(passes, resumed.ReviewedPassKeys);
    }
}
