// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoPullRequestIterationChangePagerTests
{
    [Fact]
    public async Task LoadAllAsync_SinglePage_ReturnsAllChanges()
    {
        var calls = new List<(int top, int skip)>();

        var changes = await AdoPullRequestIterationChangePager.LoadAllAsync(
            (top, skip, _) =>
            {
                calls.Add((top, skip));
                return Task.FromResult(
                    new GitPullRequestIterationChanges
                    {
                        ChangeEntries =
                        [
                            CreateChange("/src/A.cs"),
                            CreateChange("/src/B.cs"),
                        ],
                        NextSkip = 0,
                    });
            },
            CancellationToken.None);

        Assert.Equal([(AdoPullRequestIterationChangePager.MaxPageSize, 0)], calls);
        Assert.Equal(["/src/A.cs", "/src/B.cs"], changes.Select(change => change.Item!.Path));
    }

    [Fact]
    public async Task LoadAllAsync_MultiplePages_FollowsNextSkipAndConcatenatesChanges()
    {
        var calls = new List<(int top, int skip)>();

        var changes = await AdoPullRequestIterationChangePager.LoadAllAsync(
            (top, skip, _) =>
            {
                calls.Add((top, skip));
                return Task.FromResult(
                    skip switch
                    {
                        0 => new GitPullRequestIterationChanges
                        {
                            ChangeEntries = [CreateChange("/src/A.cs")],
                            NextSkip = 1,
                            NextTop = 50,
                        },
                        1 => new GitPullRequestIterationChanges
                        {
                            ChangeEntries = [CreateChange("/src/B.cs")],
                            NextSkip = 0,
                        },
                        _ => throw new InvalidOperationException($"Unexpected skip value: {skip}"),
                    });
            },
            CancellationToken.None);

        Assert.Equal([(AdoPullRequestIterationChangePager.MaxPageSize, 0), (50, 1)], calls);
        Assert.Equal(["/src/A.cs", "/src/B.cs"], changes.Select(change => change.Item!.Path));
    }

    private static GitPullRequestChange CreateChange(string path)
    {
        return new GitPullRequestChange
        {
            Item = new GitItem { Path = path },
        };
    }
}
