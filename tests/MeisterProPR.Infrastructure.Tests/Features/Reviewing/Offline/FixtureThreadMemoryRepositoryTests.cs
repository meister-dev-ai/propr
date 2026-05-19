// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class FixtureThreadMemoryRepositoryTests
{
    [Fact]
    public async Task FindSimilarAsync_WhenScenarioHasMatches_ReturnsScenarioMatchesOrderedBySimilarity()
    {
        var accessor = new ReviewEvaluationFixtureAccessor
        {
            Fixture = CreateFixture(),
            ScenarioId = "memory-good-only",
        };
        var repository = new FixtureThreadMemoryRepository(accessor);

        var matches = await repository.FindSimilarAsync(Guid.NewGuid(), [0.5f], 10, 0.1f, CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Equal("Program.cs", matches[0].FilePath);
        Assert.Equal("Article routes still need to be included in the sitemap output.", matches[0].ResolutionSummary);
        Assert.True(matches[0].SimilarityScore >= matches[1].SimilarityScore);
    }

    [Fact]
    public async Task FindByFilePathAsync_FiltersOutNonMatchingScenarioFiles()
    {
        var accessor = new ReviewEvaluationFixtureAccessor
        {
            Fixture = CreateFixture(),
            ScenarioId = "memory-good-only",
        };
        var repository = new FixtureThreadMemoryRepository(accessor);

        var matches = await repository.FindByFilePathAsync(Guid.NewGuid(), "repo", "docs/notes.md", 10, CancellationToken.None);

        Assert.Empty(matches);
    }

    private static ReviewEvaluationFixture CreateFixture()
    {
        return new ReviewEvaluationFixture(
            "fixture-sample",
            "1.0",
            new FixtureProvenance("synthetic"),
            new RepositorySnapshot(
                "feature/offline-review",
                "main",
                [new RepositoryFileEntry("Program.cs", "internal static class Program {}")],
                "sample-repository"),
            new PullRequestSnapshot(
                new CodeReviewRef(
                    new RepositoryRef(
                        new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/example"),
                        "sample-repository",
                        "sample-project",
                        "sample-project"),
                    CodeReviewPlatformKind.PullRequest,
                    "42",
                    42),
                new ReviewRevision("head-sha", "base-sha", null, null, null),
                "Sample review",
                "Offline review fixture",
                "feature/offline-review",
                "main",
                [new FixtureChangedFile("Program.cs", ChangeType.Edit, "+++ b/Program.cs", "internal static class Program {}")]),
            Scenarios:
            [
                new FixtureScenario(
                    "memory-good-only",
                    ThreadMemory: new FixtureThreadMemory(
                    [
                        new FixtureThreadMemoryMatch(
                            null,
                            101,
                            "Program.cs",
                            "Article routes still need to be included in the sitemap output.",
                            0.95f,
                            Source: MemorySource.ThreadResolved),
                        new FixtureThreadMemoryMatch(
                            null,
                            102,
                            "Program.cs",
                            "A similar sitemap change previously missed generated article pages.",
                            0.82f,
                            Source: MemorySource.ThreadResolved),
                    ])),
            ]);
    }
}
