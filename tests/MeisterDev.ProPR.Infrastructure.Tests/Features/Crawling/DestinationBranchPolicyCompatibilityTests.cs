// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

public sealed class DestinationBranchPolicyCompatibilityTests
{
    [Fact]
    public void Matches_LongBranchRetainsExistingRecursiveGlobBehavior()
    {
        var branch = "release/" + string.Join('/', Enumerable.Repeat(new string('a', 200), 4));
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("release/**");
        Assert.True(matcher.Match(branch).HasMatches);
        Assert.Equal(matcher.Match(branch).HasMatches, DestinationBranchPolicy.Create(["release/**"]).Matches(branch));
    }

    [Theory]
    [InlineData("𐐀", "𐐨")]
    [InlineData("release/𐐀*", "release/𐐨-name")]
    [InlineData("main", "MAIN")]
    [InlineData("release/*", "release/v1")]
    [InlineData("release/*", "release/v1/hotfix")]
    [InlineData("release/**", "release")]
    [InlineData("release/**", "release/v1/hotfix")]
    [InlineData("release/**/main", "release/main")]
    [InlineData("release/**/main", "release/v1/main")]
    [InlineData("**/main", "main")]
    [InlineData("**/main", "feature/main")]
    [InlineData("*", "feature/main")]
    [InlineData("**", "feature/main")]
    [InlineData("./main", "main")]
    [InlineData("/main", "main")]
    public void Matches_RetainsExistingPathGlobBehavior(string pattern, string branch)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        Assert.Equal(matcher.Match(branch).HasMatches, DestinationBranchPolicy.Create([pattern]).Matches(branch));
    }
}
