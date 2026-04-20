// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

/// <summary>
///     Unit tests for persisted ProCursor symbol-query behavior.
/// </summary>
public sealed class ProCursorSymbolQueryServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task GetSymbolInsightAsync_WithIndexedSnapshot_ReturnsDefinitionAndRelations()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource();
        var branch = source.TrackedBranches.Single();
        var snapshot = CreateReadySnapshot(source, branch, "commit-1", true);
        var symbol = new ProCursorSymbolRecord(
            Guid.NewGuid(),
            snapshot.Id,
            "csharp",
            "T:Demo.Greeter",
            "Greeter",
            "type",
            null,
            "src/Greeter.cs",
            3,
            13,
            "Demo.Greeter",
            "Greeter Demo.Greeter");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        symbolGraphRepository.SearchAsync(snapshot.Id, "Greeter", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorSymbolRecord>>([symbol]));
        symbolGraphRepository.ListEdgesAsync(
                snapshot.Id,
                symbol.SymbolKey,
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ProCursorSymbolEdge>>(
                [
                    new ProCursorSymbolEdge(
                        Guid.NewGuid(),
                        snapshot.Id,
                        symbol.SymbolKey,
                        "M:Demo.Greeter.Run",
                        "containment",
                        "src/Greeter.cs",
                        8,
                        12),
                    new ProCursorSymbolEdge(
                        Guid.NewGuid(),
                        snapshot.Id,
                        "M:Demo.Program.Main",
                        symbol.SymbolKey,
                        "reference",
                        "src/Program.cs",
                        4,
                        4),
                ]));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(ClientId, "Greeter"),
            CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal(snapshot.Id, result.SnapshotId);
        Assert.False(result.OverlayUsed);
        Assert.True(result.SupportsSymbolQueries);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Greeter", result.Symbol!.DisplayName);
        Assert.Equal("src/Greeter.cs", result.Symbol.Definition.FilePath);
        Assert.Contains(
            result.Relations,
            relation => relation.RelationKind == "containment" && relation.ToSymbol == "M:Demo.Greeter.Run");
        Assert.Contains(
            result.Relations,
            relation => relation.RelationKind == "reference" && relation.FromSymbol == "M:Demo.Program.Main");
    }

    [Fact]
    public async Task GetSymbolInsightAsync_WhenSnapshotsDoNotSupportSymbols_ReturnsUnsupportedLanguage()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource();
        var branch = source.TrackedBranches.Single();
        var snapshot = CreateReadySnapshot(source, branch, "commit-1", false);

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(ClientId, "Greeter"),
            CancellationToken.None);

        Assert.Equal("unsupportedLanguage", result.Status);
        Assert.False(result.SupportsSymbolQueries);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Relations);
    }

    [Fact]
    public async Task GetSymbolInsightAsync_WhenSymbolDoesNotExist_ReturnsNotFound()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource();
        var branch = source.TrackedBranches.Single();
        var snapshot = CreateReadySnapshot(source, branch, "commit-1", true);

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        symbolGraphRepository.SearchAsync(snapshot.Id, "MissingSymbol", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorSymbolRecord>>([]));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(ClientId, "MissingSymbol"),
            CancellationToken.None);

        Assert.Equal("notFound", result.Status);
        Assert.True(result.SupportsSymbolQueries);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Relations);
    }

    [Fact]
    public async Task GetSymbolInsightAsync_WhenEligibleSourcesExceedConfiguredLimit_OnlyScansTopSources()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var firstSource = CreateSource();
        firstSource.UpdateDefinition(
            "A Source",
            firstSource.ProviderScopePath,
            firstSource.ProviderProjectKey,
            "repo-a",
            firstSource.DefaultBranch,
            firstSource.RootPath,
            firstSource.IsEnabled,
            firstSource.SymbolMode);
        var secondSource = CreateSource();
        secondSource.UpdateDefinition(
            "B Source",
            secondSource.ProviderScopePath,
            secondSource.ProviderProjectKey,
            "repo-b",
            secondSource.DefaultBranch,
            secondSource.RootPath,
            secondSource.IsEnabled,
            secondSource.SymbolMode);
        var firstSnapshot = CreateReadySnapshot(firstSource, firstSource.TrackedBranches.Single(), "commit-a", true);
        var secondSnapshot = CreateReadySnapshot(secondSource, secondSource.TrackedBranches.Single(), "commit-b", true);
        var symbol = new ProCursorSymbolRecord(
            Guid.NewGuid(),
            secondSnapshot.Id,
            "csharp",
            "T:Demo.Greeter",
            "Greeter",
            "type",
            null,
            "src/Greeter.cs",
            3,
            12,
            "Demo.Greeter",
            "Greeter Demo.Greeter");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([firstSource, secondSource]));
        snapshotRepository.GetLatestReadyAsync(firstSource.Id, null, Arg.Any<CancellationToken>())
            .Returns(firstSnapshot);
        snapshotRepository.GetLatestReadyAsync(secondSource.Id, null, Arg.Any<CancellationToken>())
            .Returns(secondSnapshot);
        symbolGraphRepository.SearchAsync(firstSnapshot.Id, "Greeter", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorSymbolRecord>>([]));
        symbolGraphRepository.SearchAsync(secondSnapshot.Id, "Greeter", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorSymbolRecord>>([symbol]));

        var service = new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { MaxQueryResults = 5, MaxSourcesPerQuery = 1 }),
            NullLogger<ProCursorQueryService>.Instance);

        var result = await service.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(ClientId, "Greeter"),
            CancellationToken.None);

        Assert.Equal("notFound", result.Status);
        await snapshotRepository.Received(1).GetLatestReadyAsync(firstSource.Id, null, Arg.Any<CancellationToken>());
        await snapshotRepository.DidNotReceive()
            .GetLatestReadyAsync(secondSource.Id, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSymbolInsightAsync_WithReviewTargetOverlay_ReturnsOverlayResult()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var materializer = Substitute.For<IProCursorMaterializer>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();
        var source = CreateSource();
        var baseSnapshot = CreateReadySnapshot(source, source.TrackedBranches.Single(), "commit-base", true);
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "meisterpropr-procursor-overlay-query-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(baseSnapshot);
        materializer.SourceKind.Returns(ProCursorSourceKind.Repository);
        materializer.MaterializeAsync(source, Arg.Any<ProCursorTrackedBranch>(), null, Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorMaterializedSource(
                    source.Id,
                    Guid.NewGuid(),
                    "feature/branch",
                    "commit-overlay",
                    rootDirectory,
                    ["/src/Greeter.cs"]));
        symbolExtractor.ExtractAsync(
                Arg.Any<ProCursorMaterializedSource>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var overlayId = call.ArgAt<Guid>(1);
                return new ProCursorSymbolExtractionResult(
                    [
                        new ProCursorSymbolRecord(
                            Guid.NewGuid(),
                            overlayId,
                            "csharp",
                            "T:Demo.Greeter",
                            "Greeter",
                            "type",
                            null,
                            "src/Greeter.cs",
                            3,
                            12,
                            "Demo.Greeter",
                            "Greeter Demo.Greeter"),
                    ],
                    [],
                    true);
            });

        var miniIndexBuilder = new ProCursorMiniIndexBuilder(
            sourceRepository,
            snapshotRepository,
            [materializer],
            symbolExtractor,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { MiniIndexTtlMinutes = 30, MaxQueryResults = 5 }),
            NullLogger<ProCursorMiniIndexBuilder>.Instance);
        var service = new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { MaxQueryResults = 5, MiniIndexTtlMinutes = 30 }),
            NullLogger<ProCursorQueryService>.Instance,
            miniIndexBuilder);

        try
        {
            var result = await service.GetSymbolInsightAsync(
                new ProCursorSymbolQueryRequest(
                    ClientId,
                    "Greeter",
                    StateMode: "reviewTarget",
                    ReviewContext: new ProCursorReviewContextDto("repo-app", "feature/branch", 17, 3)),
                CancellationToken.None);

            Assert.Equal("complete", result.Status);
            Assert.True(result.OverlayUsed);
            Assert.Equal(baseSnapshot.Id, result.SnapshotId);
            Assert.True(result.SupportsSymbolQueries);
            Assert.NotNull(result.Symbol);
            Assert.Equal("Greeter", result.Symbol!.DisplayName);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, true);
            }
        }
    }

    private static ProCursorQueryService CreateService(
        IProCursorKnowledgeSourceRepository sourceRepository,
        IProCursorIndexSnapshotRepository snapshotRepository,
        IProCursorEmbeddingService embeddingService,
        IProCursorSymbolGraphRepository symbolGraphRepository)
    {
        return new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { MaxQueryResults = 5 }),
            NullLogger<ProCursorQueryService>.Instance);
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
