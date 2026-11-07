// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers.Common;

public sealed class DeltaFileAdditionsTests
{
    private static readonly IReadOnlyList<string> AllFiles = ["src/A.cs", "src/B.cs", "src/C.cs"];

    [Fact]
    public void WithoutRequestedFiles_TheDeltaStaysAsItIs()
    {
        IReadOnlyList<string> delta = ["src/B.cs"];

        Assert.Same(delta, DeltaFileAdditions.AddRequestedFiles(delta, AllFiles, null, path => path));
        Assert.Same(delta, DeltaFileAdditions.AddRequestedFiles(delta, AllFiles, [], path => path));
    }

    [Fact]
    public void ARequestedFileOutsideTheDelta_IsAddedAfterTheDelta()
    {
        var files = DeltaFileAdditions.AddRequestedFiles(["src/B.cs"], AllFiles, ["src/C.cs"], path => path);

        Assert.Equal(["src/B.cs", "src/C.cs"], files);
    }

    // The delta already fetches a requested file that changed, and a full fetch already contains every file.
    [Fact]
    public void ARequestedFileTheDeltaContains_IsNotAddedTwice()
    {
        Assert.Equal(["src/B.cs"], DeltaFileAdditions.AddRequestedFiles(["src/B.cs"], AllFiles, ["src/B.cs"], path => path));
        Assert.Equal(AllFiles, DeltaFileAdditions.AddRequestedFiles(AllFiles, AllFiles, ["src/C.cs"], path => path));
    }

    [Fact]
    public void ARequestedFileThePullRequestNoLongerContains_AddsNothing()
    {
        Assert.Equal(["src/B.cs"], DeltaFileAdditions.AddRequestedFiles(["src/B.cs"], AllFiles, ["src/Removed.cs"], path => path));
    }

    // Carry-forward compares paths without regard to case, and so does the addition, so the two agree on which
    // file a stored result belongs to.
    [Fact]
    public void ARequestedPath_MatchesTheListingWithoutRegardToCase()
    {
        Assert.Equal(["src/B.cs", "src/C.cs"], DeltaFileAdditions.AddRequestedFiles(["src/B.cs"], AllFiles, ["SRC/c.cs"], path => path));
    }

    [Fact]
    public void AnEntryWithoutAPath_IsNeverAdded()
    {
        IReadOnlyList<string> all = ["src/A.cs", "folder/"];

        var files = DeltaFileAdditions.AddRequestedFiles([], all, ["folder/"], path => path.EndsWith('/') ? null : path);

        Assert.Empty(files);
    }
}
