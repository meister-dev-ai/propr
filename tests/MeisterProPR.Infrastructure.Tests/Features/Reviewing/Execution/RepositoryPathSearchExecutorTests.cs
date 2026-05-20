// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class RepositoryPathSearchExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ContainsMode_ReturnsRankedMatchingPaths()
    {
        var result = await ExecuteAsync(
            new PathSearchRequest(
                "Registration",
                PathSearchModes.Contains,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository),
            ["src/ServiceRegistration.cs", "src/Other.cs"]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        var match = Assert.Single(result.Paths);
        Assert.Equal("src/ServiceRegistration.cs", match.FilePath);
        Assert.Equal(1, match.Rank);
    }

    [Fact]
    public async Task ExecuteAsync_ExactNameMode_MatchesFileNameOnly()
    {
        var result = await ExecuteAsync(
            new PathSearchRequest(
                "Foo.cs",
                PathSearchModes.ExactName,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository),
            ["src/Foo.cs", "src/FooTests.cs"]);

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        var match = Assert.Single(result.Paths);
        Assert.Equal("src/Foo.cs", match.FilePath);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersApplyToPathSearch()
    {
        var result = await ExecuteAsync(
            new PathSearchRequest(
                "Foo",
                PathSearchModes.Contains,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository,
                new CodeSearchFilterSet("csharp", PathPrefix: "src", ExcludeTests: true)),
            ["src/Foo.cs", "src/Foo.ts", "tests/FooTests.cs"]);

        Assert.Equal(RepositorySearchStatuses.Partial, result.Status);
        var match = Assert.Single(result.Paths);
        Assert.Equal("src/Foo.cs", match.FilePath);
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.FilterExcluded);
    }

    private static Task<PathSearchResult> ExecuteAsync(PathSearchRequest request, IReadOnlyList<string> tree)
    {
        return RepositoryPathSearchExecutor.ExecuteAsync(
            request,
            "feature/test",
            "main",
            null,
            (_, _) => Task.FromResult(tree),
            branch => branch,
            path => path,
            CancellationToken.None);
    }
}
