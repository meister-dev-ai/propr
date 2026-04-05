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
///     Unit tests for <see cref="ProCursorQueryService" /> covering reviewer knowledge retrieval behavior.
/// </summary>
public sealed class ProCursorKnowledgeQueryServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task AskKnowledgeAsync_WithRepositoryContextBias_ReturnsOrderedMatches()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var repositorySource = CreateSource("App Repository", ProCursorSourceKind.Repository, "repo-app");
        var wikiSource = CreateSource("Operations Wiki", ProCursorSourceKind.AdoWiki, "wiki-repo");

        var repoSnapshot = CreateReadySnapshot(repositorySource, repositorySource.TrackedBranches.Single(), "commit-repo");
        var wikiSnapshot = CreateReadySnapshot(wikiSource, wikiSource.TrackedBranches.Single(), "commit-wiki");
        var repoChunk = CreateChunk(
            repoSnapshot.Id,
            "docs/token-caching.md",
            "Token caching",
            "Token caching avoids redundant network calls by reusing a valid access token until it expires.",
            [1f, 0f]);
        var wikiChunk = CreateChunk(
            wikiSnapshot.Id,
            "wiki/operations/token-guidance.md",
            "Operational guidance",
            "Avoid redundant network calls by reusing existing cached tokens whenever possible.",
            [0.2f, 1f]);

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([repositorySource, wikiSource]));
        snapshotRepository.GetLatestReadyAsync(repositorySource.Id, null, Arg.Any<CancellationToken>())
            .Returns(repoSnapshot);
        snapshotRepository.GetLatestReadyAsync(wikiSource.Id, null, Arg.Any<CancellationToken>())
            .Returns(wikiSnapshot);
        snapshotRepository.ListKnowledgeChunksAsync(repoSnapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([repoChunk]));
        snapshotRepository.ListKnowledgeChunksAsync(wikiSnapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([wikiChunk]));
        embeddingService.GenerateEmbeddingsAsync(ClientId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<ProCursorEmbeddingUsageContext?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f]]));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(
            ClientId,
            "How do we avoid redundant network calls with token caching?",
            RepositoryContext: new ProCursorRepositoryContextDto(
                repositorySource.OrganizationUrl,
                repositorySource.ProjectId,
                repositorySource.RepositoryId,
                repositorySource.DefaultBranch),
            MaxResults: 1));

        Assert.Equal("complete", result.Status);
        Assert.Single(result.Results);
        Assert.Equal(repositorySource.Id, result.Results[0].SourceId);
        Assert.Equal("hybrid", result.Results[0].MatchKind);
        Assert.Equal("fresh", result.Results[0].FreshnessStatus);
        await embeddingService.Received(1).GenerateEmbeddingsAsync(
            ClientId,
            Arg.Is<IReadOnlyList<string>>(inputs => inputs.Count == 1 && inputs[0].Contains("token caching", StringComparison.OrdinalIgnoreCase)),
            Arg.Is<ProCursorEmbeddingUsageContext?>(context =>
                context != null &&
                context.ProCursorSourceId == repositorySource.Id &&
                context.SourceDisplayNameSnapshot == repositorySource.DisplayName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskKnowledgeAsync_WhenSnapshotLagsBranchHead_ReturnsStaleStatus()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = CreateUnavailableEmbeddingService();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource("App Repository", ProCursorSourceKind.Repository, "repo-app", lastSeenCommitSha: "commit-newer");
        var snapshot = CreateReadySnapshot(source, source.TrackedBranches.Single(), "commit-old");
        source.TrackedBranches.Single().RecordSeenCommit("commit-newer");
        var chunk = CreateChunk(snapshot.Id, "src/Auth/TokenCache.cs", "TokenCache", "Token caching reduces redundant network calls against Azure DevOps.");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        snapshotRepository.ListKnowledgeChunksAsync(snapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([chunk]));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(
            ClientId,
            "Where do we explain token caching?"));

        Assert.Equal("stale", result.Status);
        Assert.Single(result.Results);
        Assert.Equal("stale", result.Results[0].FreshnessStatus);
    }

    [Fact]
    public async Task AskKnowledgeAsync_WhenEligibleSourcesExceedConfiguredLimit_OnlyScansTopSources()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = CreateUnavailableEmbeddingService();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var firstSource = CreateSource("A Source", ProCursorSourceKind.Repository, "repo-a");
        var secondSource = CreateSource("B Source", ProCursorSourceKind.AdoWiki, "repo-b");
        var firstSnapshot = CreateReadySnapshot(firstSource, firstSource.TrackedBranches.Single(), "commit-a");
        var secondSnapshot = CreateReadySnapshot(secondSource, secondSource.TrackedBranches.Single(), "commit-b");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([firstSource, secondSource]));
        snapshotRepository.GetLatestReadyAsync(firstSource.Id, null, Arg.Any<CancellationToken>())
            .Returns(firstSnapshot);
        snapshotRepository.GetLatestReadyAsync(secondSource.Id, null, Arg.Any<CancellationToken>())
            .Returns(secondSnapshot);
        snapshotRepository.ListKnowledgeChunksAsync(firstSnapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([
                CreateChunk(firstSnapshot.Id, "docs/auth.md", "Authentication", "JWT validation details only.")
            ]));
        snapshotRepository.ListKnowledgeChunksAsync(secondSnapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([
                CreateChunk(secondSnapshot.Id, "docs/token-caching.md", "Token caching", "Token caching avoids redundant network calls.")
            ]));

        var service = CreateService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            new ProCursorOptions { MaxQueryResults = 5, MaxSourcesPerQuery = 1 });

        var result = await service.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(
            ClientId,
            "How is token caching handled?"));

        Assert.Equal("noResult", result.Status);
        await snapshotRepository.Received(1).GetLatestReadyAsync(firstSource.Id, null, Arg.Any<CancellationToken>());
        await snapshotRepository.DidNotReceive().GetLatestReadyAsync(secondSource.Id, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskKnowledgeAsync_WhenNoChunksMatch_ReturnsNoResult()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = CreateUnavailableEmbeddingService();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource("App Repository", ProCursorSourceKind.Repository, "repo-app");
        var snapshot = CreateReadySnapshot(source, source.TrackedBranches.Single(), "commit-1");
        var chunk = CreateChunk(snapshot.Id, "docs/auth.md", "Authentication", "This document explains JWT claims and PAT validation.");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        snapshotRepository.ListKnowledgeChunksAsync(snapshot.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeChunk>>([chunk]));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(
            ClientId,
            "How do we coordinate review freshness metadata?"));

        Assert.Equal("noResult", result.Status);
        Assert.Empty(result.Results);
        Assert.NotNull(result.NoResultReason);
    }

    [Fact]
    public async Task AskKnowledgeAsync_WhenNoReadySnapshotsExist_ReturnsUnavailable()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var embeddingService = CreateUnavailableEmbeddingService();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var source = CreateSource("App Repository", ProCursorSourceKind.Repository, "repo-app");

        sourceRepository.ListByClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        snapshotRepository.GetLatestReadyAsync(source.Id, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProCursorIndexSnapshot?>(null));

        var service = CreateService(sourceRepository, snapshotRepository, embeddingService, symbolGraphRepository);

        var result = await service.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(
            ClientId,
            "What does ProCursor know about onboarding?"));

        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Results);
        Assert.Contains("No ready ProCursor snapshots", result.NoResultReason, StringComparison.Ordinal);
    }

    private static ProCursorQueryService CreateService(
        IProCursorKnowledgeSourceRepository sourceRepository,
        IProCursorIndexSnapshotRepository snapshotRepository,
        IProCursorEmbeddingService embeddingService,
        IProCursorSymbolGraphRepository symbolGraphRepository,
        ProCursorOptions? options = null)
    {
        return new ProCursorQueryService(
            sourceRepository,
            snapshotRepository,
            embeddingService,
            symbolGraphRepository,
            Microsoft.Extensions.Options.Options.Create(options ?? new ProCursorOptions { MaxQueryResults = 5 }),
            NullLogger<ProCursorQueryService>.Instance);
    }

    private static IProCursorEmbeddingService CreateUnavailableEmbeddingService()
    {
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        embeddingService.GenerateEmbeddingsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ProCursorEmbeddingUsageContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<float[]>>(new InvalidOperationException("no_embedding_connection_configured")));
        return embeddingService;
    }

    private static ProCursorKnowledgeSource CreateSource(
        string displayName,
        ProCursorSourceKind sourceKind,
        string repositoryId,
        string? lastSeenCommitSha = null)
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            displayName,
            sourceKind,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
            "main",
            null,
            true,
            "auto");

        var branch = source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        branch.RecordIndexedCommit("commit-old");

        if (lastSeenCommitSha is not null)
        {
            branch.RecordSeenCommit(lastSeenCommitSha);
        }

        return source;
    }

    private static ProCursorIndexSnapshot CreateReadySnapshot(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch branch,
        string commitSha)
    {
        branch.RecordIndexedCommit(commitSha);
        branch.RecordSeenCommit(commitSha);
        var snapshot = new ProCursorIndexSnapshot(Guid.NewGuid(), source.Id, branch.Id, commitSha, "full");
        snapshot.MarkReady(1, 1, 0, false);
        return snapshot;
    }

    private static ProCursorKnowledgeChunk CreateChunk(
        Guid snapshotId,
        string sourcePath,
        string title,
        string contentText,
        float[]? embeddingVector = null)
    {
        return new ProCursorKnowledgeChunk(
            Guid.NewGuid(),
            snapshotId,
            sourcePath,
            "wiki_page",
            title,
            0,
            null,
            null,
            $"hash-{Guid.NewGuid():N}",
            contentText,
            embeddingVector ?? [0.1f, 0.2f]);
    }
}
