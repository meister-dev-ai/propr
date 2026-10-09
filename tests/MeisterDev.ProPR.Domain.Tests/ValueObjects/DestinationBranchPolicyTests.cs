// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class DestinationBranchPolicyTests
{
    [Fact]
    public void Matches_RecursivePatternAcceptsLongMultiSegmentBranch()
    {
        var branch = "release/" + string.Join('/', Enumerable.Repeat(new string('a', 200), 4));
        Assert.True(DestinationBranchPolicy.Create(["release/**"]).Matches(branch));
    }

    [Fact]
    public void Matches_ReferencePrefixDoesNotReduceMaximumBranchNameLength()
    {
        var branch = new string('a', DestinationBranchPolicy.MaximumPatternLength);
        Assert.True(DestinationBranchPolicy.Create([branch]).Matches("refs/heads/" + branch));
    }

    [Theory]
    [InlineData("𐐀", "𐐨", true)]
    [InlineData("release/𐐀*", "release/𐐨-name", true)]
    [InlineData("main", "MAIN", true)]
    [InlineData("refs/heads/main", "refs/heads/MAIN", true)]
    [InlineData("main", "feature/main", false)]
    [InlineData("release/*", "release/v1", true)]
    [InlineData("release/*", "release/v1/hotfix", false)]
    [InlineData("release/**", "release/v1/hotfix", true)]
    [InlineData("**/main", "main", true)]
    [InlineData("**/main", "feature/main", true)]
    [InlineData("release/*", null, false)]
    public void Matches_PreservesCaseInsensitivePathGlobSemantics(string pattern, string? branch, bool expected)
    {
        Assert.Equal(expected, DestinationBranchPolicy.Create([pattern]).Matches(branch));
    }

    [Fact]
    public void EmptyPolicy_AcceptsEveryBranchIncludingUnavailableData()
    {
        Assert.True(DestinationBranchPolicy.Create([]).Matches(null));
        Assert.True(DestinationBranchPolicy.Create([]).Matches("feature/any"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("refs/heads/")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("./")]
    [InlineData("///")]
    [InlineData("././")]
    [InlineData("refs/heads/./")]
    [InlineData("main\nbranch")]
    [InlineData("main\0branch")]
    public void Create_RejectsEmptyOrControlPatterns(string pattern)
    {
        Assert.Throws<ArgumentException>(() => DestinationBranchPolicy.Create([pattern]));
    }

    [Fact]
    public void Create_RejectsCanonicalDuplicatesAndBounds()
    {
        Assert.Throws<ArgumentException>(() => DestinationBranchPolicy.Create(["main", " refs/heads/MAIN "]));
        Assert.Throws<ArgumentException>(() => DestinationBranchPolicy.Create(Enumerable.Range(0, 101).Select(i => "branch" + i).ToArray()));
        Assert.Throws<ArgumentException>(() => DestinationBranchPolicy.Create([new string('a', 513)]));
        Assert.Single(DestinationBranchPolicy.Create([new string('a', 512)]).Patterns);
        Assert.Equal(100, DestinationBranchPolicy.Create(Enumerable.Range(0, 100).Select(i => "branch" + i).ToArray()).Patterns.Count);
    }

    [Theory]
    [InlineData("main", "./main")]
    [InlineData("main", "/main")]
    [InlineData("main", "main/")]
    [InlineData("release/main", "release//./MAIN")]
    public void Create_RejectsEffectiveDuplicates(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => DestinationBranchPolicy.Create([first, second]));
        Assert.False(DestinationBranchPolicy.TryCreate([first, second], out var policy));
        Assert.Null(policy);
    }

    [Theory]
    [InlineData("./main", "main")]
    [InlineData("/main", "main")]
    [InlineData("release//./main", "release/main")]
    public void Create_RetainsValidPatternSpellingAndBehavior(string pattern, string branch)
    {
        var input = new[] { pattern };
        var policy = DestinationBranchPolicy.Create(input);
        Assert.Equal(pattern, Assert.Single(policy.Patterns));
        Assert.Equal(pattern, input[0]);
        Assert.True(policy.Matches(branch));
    }

    [Fact]
    public void Create_NormalizesBranchReferencesWithoutMutatingInput()
    {
        var input = new[] { " refs/heads/main ", "release/*" };
        var policy = DestinationBranchPolicy.Create(input);
        input[0] = "other";
        Assert.Equal(["main", "release/*"], policy.Patterns);
    }
}
