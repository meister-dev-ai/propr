// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

/// <summary>
///     Unit tests for review-target ProCursor overlay construction.
/// </summary>
public sealed class ProCursorMiniIndexBuilderTests
{
    private static readonly Guid ClientId = Guid.Parse("50000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task BuildAsync_WithReviewTargetContext_ReturnsOverlayAndCleansTempWorkspace()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var materializer = Substitute.For<IProCursorMaterializer>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();
        var source = CreateSource();
        var baseSnapshot = CreateReadySnapshot(source, source.TrackedBranches.Single(), "commit-base", supportsSymbolQueries: true);
        var rootDirectory = Path.Combine(Path.GetTempPath(), "meisterpropr-procursor-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(baseSnapshot);
        materializer.SourceKind.Returns(ProCursorSourceKind.Repository);
        materializer.MaterializeAsync(source, Arg.Any<ProCursorTrackedBranch>(), null, Arg.Any<CancellationToken>())
            .Returns(new ProCursorMaterializedSource(
                source.Id,
                Guid.NewGuid(),
                "feature/branch",
                "commit-overlay",
                rootDirectory,
                ["/src/Greeter.cs"]));
        symbolExtractor.ExtractAsync(Arg.Any<ProCursorMaterializedSource>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var snapshotId = call.ArgAt<Guid>(1);
                return new ProCursorSymbolExtractionResult(
                    [new ProCursorSymbolRecord(
                        Guid.NewGuid(),
                        snapshotId,
                        "csharp",
                        "T:Demo.Greeter",
                        "Greeter",
                        "type",
                        null,
                        "src/Greeter.cs",
                        3,
                        12,
                        "Demo.Greeter",
                        "Greeter Demo.Greeter")],
                    [],
                    true,
                    null);
            });

        var builder = new ProCursorMiniIndexBuilder(
            sourceRepository,
            snapshotRepository,
            [materializer],
            symbolExtractor,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { MiniIndexTtlMinutes = 30 }),
            NullLogger<ProCursorMiniIndexBuilder>.Instance);

        var overlay = await builder.BuildAsync(
            new ProCursorSymbolQueryRequest(
                ClientId,
                "Greeter",
                StateMode: "reviewTarget",
                ReviewContext: new ProCursorReviewContextDto("repo-app", "feature/branch", 17, 3)),
            CancellationToken.None);

        Assert.NotNull(overlay);
        Assert.Equal(baseSnapshot.Id, overlay!.BaseSnapshotId);
        Assert.True(overlay.SupportsSymbolQueries);
        Assert.Equal("fresh", overlay.FreshnessStatus);
        Assert.Single(overlay.Symbols);
        Assert.True(overlay.ExpiresAt > overlay.BuiltAt);
        Assert.False(Directory.Exists(rootDirectory));
    }

    private static ProCursorKnowledgeSource CreateSource()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            "App Repository",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-app",
            "main",
            null,
            true,
            "auto");
        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        return source;
    }

    private static ProCursorIndexSnapshot CreateReadySnapshot(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch branch,
        string commitSha,
        bool supportsSymbolQueries)
    {
        branch.RecordSeenCommit(commitSha);
        branch.RecordIndexedCommit(commitSha);
        var snapshot = new ProCursorIndexSnapshot(Guid.NewGuid(), source.Id, branch.Id, commitSha, "full");
        snapshot.MarkReady(1, 1, 2, supportsSymbolQueries);
        return snapshot;
    }
}
