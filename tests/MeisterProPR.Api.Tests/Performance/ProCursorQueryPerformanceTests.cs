// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Performance;

public sealed class ProCursorQueryPerformanceTests
{
    private static readonly Guid ClientId = Guid.Parse("71000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task KnowledgeQuery_P95Latency_StaysWithinSc001Budget()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();

        var sources = new List<ProCursorKnowledgeSource>();
        var snapshotsBySourceId = new Dictionary<Guid, ProCursorIndexSnapshot>();
        var chunksBySnapshotId = new Dictionary<Guid, IReadOnlyList<ProCursorKnowledgeChunk>>();

        for (var index = 1; index <= 20; index++)
        {
            var source = CreateSource($"Source {index:00}", $"repo-{index:00}");
            var snapshot = CreateReadySnapshot(source, source.TrackedBranches.Single(), $"commit-{index:00}", true);
            var chunks = CreateKnowledgeChunks(snapshot.Id, index == 20);

            sources.Add(source);
            snapshotsBySourceId[source.Id] = snapshot;
            chunksBySnapshotId[snapshot.Id] = chunks;
        }

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>(sources));
        snapshotRepository.GetLatestReadyAsync(Arg.Any<Guid>(), null, Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ProCursorIndexSnapshot?>(snapshotsBySourceId[call.ArgAt<Guid>(0)]));
        snapshotRepository.ListKnowledgeChunksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(chunksBySnapshotId[call.ArgAt<Guid>(0)]));
        embeddingService.GenerateEmbeddingsAsync(
                ClientId,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<ProCursorEmbeddingUsageContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f]]));

        var service = new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Options.Create(
                new ProCursorOptions
                {
                    MaxQueryResults = 5,
                    MaxSourcesPerQuery = 20,
                }),
            NullLogger<ProCursorQueryService>.Instance);

        var request = new ProCursorKnowledgeQueryRequest(
            ClientId,
            "How do we avoid redundant network calls with token caching?",
            MaxResults: 5);

        _ = await service.AskKnowledgeAsync(request, CancellationToken.None);
        var (p95, lastResult) = await MeasureP95Async(
            25,
            () => service.AskKnowledgeAsync(request, CancellationToken.None));

        Assert.Equal("complete", lastResult.Status);
        Assert.NotEmpty(lastResult.Results);
        Assert.True(
            p95 < TimeSpan.FromSeconds(10),
            $"Expected ProCursor knowledge-query p95 latency to stay below 10 seconds for SC-001, but observed {p95}.");
    }

    [Fact]
    public async Task SymbolQuery_P95Latency_StaysWithinSc002Budget()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();

        var sources = new List<ProCursorKnowledgeSource>();
        var snapshotsBySourceId = new Dictionary<Guid, ProCursorIndexSnapshot>();
        var symbolsBySnapshotId = new Dictionary<Guid, IReadOnlyList<ProCursorSymbolRecord>>();
        var relationsBySnapshotId = new Dictionary<Guid, IReadOnlyList<ProCursorSymbolEdge>>();

        for (var index = 1; index <= 20; index++)
        {
            var source = CreateSource($"Source {index:00}", $"repo-{index:00}");
            var snapshot = CreateReadySnapshot(
                source,
                source.TrackedBranches.Single(),
                $"symbol-commit-{index:00}",
                true);
            var containsTarget = index == 20;

            sources.Add(source);
            snapshotsBySourceId[source.Id] = snapshot;
            symbolsBySnapshotId[snapshot.Id] = containsTarget
                ?
                [
                    new ProCursorSymbolRecord(
                        Guid.NewGuid(),
                        snapshot.Id,
                        "csharp",
                        "T:Demo.BillingCoordinator",
                        "BillingCoordinator",
                        "type",
                        null,
                        "src/BillingCoordinator.cs",
                        5,
                        29,
                        "Demo.BillingCoordinator",
                        "BillingCoordinator Demo.BillingCoordinator"),
                ]
                : [];
            relationsBySnapshotId[snapshot.Id] = containsTarget
                ?
                [
                    new ProCursorSymbolEdge(
                        Guid.NewGuid(),
                        snapshot.Id,
                        "T:Demo.BillingCoordinator",
                        "M:Demo.BillingCoordinator.RunAsync",
                        "containment",
                        "src/BillingCoordinator.cs",
                        12,
                        24),
                    new ProCursorSymbolEdge(
                        Guid.NewGuid(),
                        snapshot.Id,
                        "M:Demo.Program.Main",
                        "T:Demo.BillingCoordinator",
                        "reference",
                        "src/Program.cs",
                        8,
                        8),
                ]
                : [];
        }

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>(sources));
        snapshotRepository.GetLatestReadyAsync(Arg.Any<Guid>(), null, Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ProCursorIndexSnapshot?>(snapshotsBySourceId[call.ArgAt<Guid>(0)]));
        symbolGraphRepository.SearchAsync(
                Arg.Any<Guid>(),
                "BillingCoordinator",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(symbolsBySnapshotId[call.ArgAt<Guid>(0)]));
        symbolGraphRepository.ListEdgesAsync(
                Arg.Any<Guid>(),
                "T:Demo.BillingCoordinator",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(relationsBySnapshotId[call.ArgAt<Guid>(0)]));

        var service = new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Options.Create(
                new ProCursorOptions
                {
                    MaxQueryResults = 5,
                    MaxSourcesPerQuery = 20,
                }),
            NullLogger<ProCursorQueryService>.Instance);

        var request = new ProCursorSymbolQueryRequest(ClientId, "BillingCoordinator", MaxRelations: 10);

        _ = await service.GetSymbolInsightAsync(request, CancellationToken.None);
        var (p95, lastResult) = await MeasureP95Async(
            25,
            () => service.GetSymbolInsightAsync(request, CancellationToken.None));

        Assert.Equal("complete", lastResult.Status);
        Assert.NotNull(lastResult.Symbol);
        Assert.True(
            p95 < TimeSpan.FromSeconds(5),
            $"Expected ProCursor symbol-query p95 latency to stay below 5 seconds for SC-002, but observed {p95}.");
    }

    private static ProCursorKnowledgeSource CreateSource(string displayName, string repositoryId)
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            displayName,
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
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

        var snapshot = new ProCursorIndexSnapshot(
            Guid.NewGuid(),
            source.Id,
            branch.Id,
            commitSha,
            "full");
        snapshot.MarkReady(16, 16, supportsSymbolQueries ? 8 : 0, supportsSymbolQueries);
        return snapshot;
    }

    private static IReadOnlyList<ProCursorKnowledgeChunk> CreateKnowledgeChunks(Guid snapshotId, bool includeMatch)
    {
        var chunks = new List<ProCursorKnowledgeChunk>();

        for (var index = 0; index < 8; index++)
        {
            var contentText = includeMatch && index == 0
                ? "Token caching avoids redundant network calls by reusing a valid access token until it expires."
                : $"Operational note {index}: authentication and reviewer workflows stay isolated per client.";
            var embedding = includeMatch && index == 0 ? new[] { 1f, 0f } : new[] { 0f, 1f };

            chunks.Add(
                new ProCursorKnowledgeChunk(
                    Guid.NewGuid(),
                    snapshotId,
                    $"docs/doc-{index}.md",
                    "wiki_page",
                    $"Document {index}",
                    index,
                    null,
                    null,
                    $"hash-{snapshotId:N}-{index}",
                    contentText,
                    embedding));
        }

        return chunks;
    }

    private static async Task<(TimeSpan P95, TResult LastResult)> MeasureP95Async<TResult>(
        int iterations,
        Func<Task<TResult>> operation)
    {
        var samples = new List<TimeSpan>(iterations);
        var lastResult = default(TResult);

        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            lastResult = await operation();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed);
        }

        var ordered = samples.OrderBy(sample => sample).ToArray();
        var p95Index = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95d) - 1);
        return (ordered[p95Index], lastResult!);
    }
}
