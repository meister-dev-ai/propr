// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

/// <summary>
///     Unit tests for <see cref="ProCursorIndexCoordinator" /> covering durable job execution.
/// </summary>
public sealed class ProCursorIndexCoordinatorTests
{
    [Fact]
    public async Task ExecuteNextJobAsync_WithPendingJob_PersistsReadySnapshotAndCompletesJob()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var materializer = Substitute.For<IProCursorMaterializer>();
        var chunkExtractor = Substitute.For<IProCursorChunkExtractor>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();

        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            "App Repository",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-app",
            "main",
            null,
            true,
            "auto");
        var trackedBranch = source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.Manual, true);
        var job = new ProCursorIndexJob(Guid.NewGuid(), source.Id, trackedBranch.Id, null, "refresh", "dedup-key");

        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "meisterpropr-procursor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        materializer.SourceKind.Returns(ProCursorSourceKind.Repository);
        materializer.MaterializeAsync(source, trackedBranch, null, Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorMaterializedSource(
                    source.Id,
                    trackedBranch.Id,
                    trackedBranch.BranchName,
                    "commit-1",
                    rootDirectory,
                    ["/src/Program.cs"]));
        chunkExtractor.ExtractAsync(source, Arg.Any<ProCursorMaterializedSource>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ProCursorExtractedChunk>>(
                [
                    new ProCursorExtractedChunk(
                        "/src/Program.cs",
                        "code_file",
                        "Program.cs",
                        0,
                        1,
                        3,
                        "hash-1",
                        "Token caching keeps Azure DevOps calls bounded."),
                ]));
        embeddingService.EnsureConfigurationAsync(source.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        embeddingService.NormalizeChunksAsync(
                source.ClientId,
                Arg.Any<IReadOnlyList<ProCursorExtractedChunk>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Task.FromResult(callInfo.ArgAt<IReadOnlyList<ProCursorExtractedChunk>>(1)));
        embeddingService.GenerateEmbeddingsAsync(
                source.ClientId,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<ProCursorEmbeddingUsageContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f]]));
        symbolExtractor.ExtractAsync(
                Arg.Any<ProCursorMaterializedSource>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProCursorSymbolExtractionResult([], [], false, "symbol_extraction_not_configured")));

        jobRepository.GetNextPendingAsync(Arg.Any<CancellationToken>())
            .Returns(job);
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);
        sourceRepository.GetBySourceIdAsync(source.Id, Arg.Any<CancellationToken>())
            .Returns(source);
        snapshotRepository.GetLatestReadyAsync(source.Id, trackedBranch.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProCursorIndexSnapshot?>(null));

        var coordinator = new ProCursorIndexCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            symbolGraphRepository,
            [materializer],
            chunkExtractor,
            embeddingService,
            symbolExtractor,
            NullLogger<ProCursorIndexCoordinator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions()));

        var handled = await coordinator.ExecuteNextJobAsync(CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status);
        Assert.Equal("commit-1", trackedBranch.LastIndexedCommitSha);
        Assert.False(Directory.Exists(rootDirectory));
        await snapshotRepository.Received(1)
            .AddAsync(
                Arg.Is<ProCursorIndexSnapshot>(snapshot =>
                    snapshot.KnowledgeSourceId == source.Id &&
                    snapshot.TrackedBranchId == trackedBranch.Id &&
                    snapshot.CommitSha == "commit-1"),
                Arg.Any<CancellationToken>());
        await snapshotRepository.Received(1)
            .ReplaceKnowledgeChunksAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyList<ProCursorKnowledgeChunk>>(chunks =>
                    chunks.Count == 1 &&
                    chunks[0].SourcePath == "/src/Program.cs" &&
                    chunks[0].ContentText.Contains("Token caching", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        await embeddingService.Received(1)
            .GenerateEmbeddingsAsync(
                source.ClientId,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<ProCursorEmbeddingUsageContext?>(context =>
                    context != null &&
                    context.ProCursorSourceId == source.Id &&
                    context.IndexJobId == job.Id &&
                    context.RequestIdPrefix == $"pcidx:{job.Id:N}" &&
                    context.InputContexts != null &&
                    context.InputContexts.Count == 1 &&
                    context.InputContexts[0].SourcePath == "/src/Program.cs"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenCapabilityUnavailable_FailsJobWithoutProcessing()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var licensingService = Substitute.For<ILicensingCapabilityService>();
        var job = new ProCursorIndexJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "refresh", "dedup-key");

        licensingService.GetCapabilityAsync(PremiumCapabilityKey.ProCursor, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new CapabilitySnapshot(
                        PremiumCapabilityKey.ProCursor,
                        PremiumCapabilityKey.ProCursor,
                        true,
                        true,
                        PremiumCapabilityOverrideState.Default,
                        false,
                        "ProCursor requires a premium license.")));

        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var coordinator = new ProCursorIndexCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            Substitute.For<IProCursorSymbolGraphRepository>(),
            Enumerable.Empty<IProCursorMaterializer>(),
            Substitute.For<IProCursorChunkExtractor>(),
            Substitute.For<IProCursorEmbeddingService>(),
            Substitute.For<IProCursorSymbolExtractor>(),
            NullLogger<ProCursorIndexCoordinator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions()),
            licensingService);

        var handled = await coordinator.ExecuteJobAsync(job.Id, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(ProCursorIndexJobStatus.Failed, job.Status);
        await sourceRepository.DidNotReceive().GetBySourceIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await snapshotRepository.DidNotReceive().AddAsync(Arg.Any<ProCursorIndexSnapshot>(), Arg.Any<CancellationToken>());
    }
}
