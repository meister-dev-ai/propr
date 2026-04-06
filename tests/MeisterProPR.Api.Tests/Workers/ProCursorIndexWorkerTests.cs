// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Tests.Workers;

public sealed class ProCursorIndexWorkerTests
{
    [Fact]
    public async Task Worker_RetriesTransientFailure_AndEventuallyCompletesJob()
    {
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = BuildServiceProvider(
            new FlakyRepositoryMaterializer(success),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 1,
                RefreshPollSeconds = 1,
                ChunkTargetLines = 50,
            });

        var sourceId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedSourceAndJobAsync(provider, sourceId, branchId, jobId, "source-a", "main", "text_only");

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await success.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var job = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);

        Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, await db.ProCursorIndexSnapshots.CountAsync(snapshot => snapshot.KnowledgeSourceId == sourceId));
    }

    [Fact]
    public async Task Worker_ProcessesDifferentSourcesInParallel_AndLeavesSameSourceFollowUpPending()
    {
        var sourceAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSourceA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = BuildServiceProvider(
            new BlockingRepositoryMaterializer(sourceAStarted, sourceBStarted, releaseSourceA),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 2,
                RefreshPollSeconds = 3600,
                ChunkTargetLines = 50,
            });

        var sourceAId = Guid.NewGuid();
        var sourceABranchId = Guid.NewGuid();
        var sourceBId = Guid.NewGuid();
        var sourceBBranchId = Guid.NewGuid();
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        var thirdJobId = Guid.NewGuid();

        await SeedSourceAndJobAsync(provider, sourceAId, sourceABranchId, firstJobId, "source-a", "main", "text_only", dedupSuffix: "1");
        await SeedJobAsync(provider, sourceAId, sourceABranchId, secondJobId, dedupSuffix: "2");
        await SeedSourceAndJobAsync(provider, sourceBId, sourceBBranchId, thirdJobId, "source-b", "main", "text_only", dedupSuffix: "3");

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await sourceAStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sourceBStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseSourceA.TrySetResult();
            await WaitForJobsAsync(provider, firstJobId, thirdJobId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var jobs = await db.ProCursorIndexJobs
            .Where(job => job.Id == firstJobId || job.Id == secondJobId || job.Id == thirdJobId)
            .OrderBy(job => job.QueuedAt)
            .ToListAsync();

        Assert.Equal(ProCursorIndexJobStatus.Completed, jobs.Single(job => job.Id == firstJobId).Status);
        Assert.Equal(ProCursorIndexJobStatus.Pending, jobs.Single(job => job.Id == secondJobId).Status);
        Assert.Equal(ProCursorIndexJobStatus.Completed, jobs.Single(job => job.Id == thirdJobId).Status);
    }

    [Fact]
    public async Task Worker_WithIncompleteProCursorGraph_StartsAndCompletesCycleWithoutThrowing()
    {
        using var provider = BuildIncompleteGraphServiceProvider();
        var worker = provider.GetRequiredService<ProCursorIndexWorker>();

        await worker.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(100);

            Assert.True(worker.IsRunning);
            Assert.NotNull(worker.LastCycleStartedAt);
            Assert.NotNull(worker.LastCycleCompletedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider BuildServiceProvider(IProCursorMaterializer materializer, ProCursorOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddDbContext<MeisterProPRDbContext>(builder => builder.UseInMemoryDatabase(databaseName));

        services.AddScoped<IProCursorKnowledgeSourceRepository, ProCursorKnowledgeSourceRepository>();
        services.AddScoped<IProCursorIndexJobRepository, ProCursorIndexJobRepository>();
        services.AddScoped<IProCursorIndexSnapshotRepository, ProCursorIndexSnapshotRepository>();
        services.AddScoped<ProCursorSymbolGraphRepository>();
        services.AddScoped<IProCursorSymbolGraphRepository>(sp => sp.GetRequiredService<ProCursorSymbolGraphRepository>());

        services.AddSingleton<IProCursorMaterializer>(materializer);
        services.AddSingleton<IProCursorChunkExtractor, EmptyChunkExtractor>();
        services.AddSingleton<IProCursorEmbeddingService, EmptyEmbeddingService>();
        services.AddSingleton<IProCursorSymbolExtractor, EmptySymbolExtractor>();
        services.AddSingleton<IProCursorTrackedBranchChangeDetector, NoOpTrackedBranchChangeDetector>();

        services.AddScoped<ProCursorRefreshScheduler>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddSingleton<IOptions<ProCursorOptions>>(Options.Create(options));
        services.AddSingleton<ProCursorIndexWorker>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceProvider BuildIncompleteGraphServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ProCursorRefreshScheduler>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddSingleton<IOptions<ProCursorOptions>>(Options.Create(new ProCursorOptions
        {
            MaxIndexConcurrency = 1,
            RefreshPollSeconds = 1,
            ChunkTargetLines = 50,
        }));
        services.AddSingleton<ProCursorIndexWorker>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedSourceAndJobAsync(
        ServiceProvider provider,
        Guid sourceId,
        Guid branchId,
        Guid jobId,
        string repositoryId,
        string branchName,
        string symbolMode,
        string dedupSuffix = "1")
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        db.Clients.Add(new ClientRecord
        {
            Id = sourceId,
            DisplayName = repositoryId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var source = new ProCursorKnowledgeSource(
            sourceId,
            sourceId,
            repositoryId,
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
            branchName,
            null,
            true,
            symbolMode);
        source.AddTrackedBranch(branchId, branchName, ProCursorRefreshTriggerMode.Manual, true);

        db.ProCursorKnowledgeSources.Add(source);
        db.ProCursorIndexJobs.Add(new ProCursorIndexJob(
            jobId,
            sourceId,
            branchId,
            null,
            "refresh",
            $"{sourceId:N}:{branchId:N}:{dedupSuffix}"));
        await db.SaveChangesAsync();
    }

    private static async Task SeedJobAsync(
        ServiceProvider provider,
        Guid sourceId,
        Guid branchId,
        Guid jobId,
        string dedupSuffix)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        db.ProCursorIndexJobs.Add(new ProCursorIndexJob(
            jobId,
            sourceId,
            branchId,
            null,
            "refresh",
            $"{sourceId:N}:{branchId:N}:{dedupSuffix}"));
        await db.SaveChangesAsync();
    }

    private static async Task WaitForJobsAsync(ServiceProvider provider, params Guid[] jobIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var allCompleted = await db.ProCursorIndexJobs
                .Where(job => jobIds.Contains(job.Id))
                .AllAsync(job => job.Status == ProCursorIndexJobStatus.Completed);

            if (allCompleted)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for ProCursor jobs to complete.");
    }

    private sealed class NoOpTrackedBranchChangeDetector : IProCursorTrackedBranchChangeDetector
    {
        public Task<string?> GetLatestCommitShaAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            CancellationToken ct = default)
        {
            return Task.FromResult<string?>(trackedBranch.LastSeenCommitSha ?? trackedBranch.LastIndexedCommitSha);
        }
    }

    private sealed class EmptyChunkExtractor : IProCursorChunkExtractor
    {
        public Task<IReadOnlyList<ProCursorExtractedChunk>> ExtractAsync(
            ProCursorKnowledgeSource source,
            ProCursorMaterializedSource materializedSource,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ProCursorExtractedChunk>>([]);
        }
    }

    private sealed class EmptyEmbeddingService : IProCursorEmbeddingService
    {
        public Task EnsureConfigurationAsync(Guid clientId, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProCursorExtractedChunk>> NormalizeChunksAsync(
            Guid clientId,
            IReadOnlyList<ProCursorExtractedChunk> chunks,
            CancellationToken ct = default)
        {
            return Task.FromResult(chunks);
        }

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
            Guid clientId,
            IReadOnlyList<string> inputs,
            ProCursorEmbeddingUsageContext? usageContext = null,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<float[]>>([]);
        }
    }

    private sealed class EmptySymbolExtractor : IProCursorSymbolExtractor
    {
        public Task<ProCursorSymbolExtractionResult> ExtractAsync(
            ProCursorMaterializedSource materializedSource,
            Guid snapshotId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ProCursorSymbolExtractionResult([], [], false, "text_only"));
        }
    }

    private sealed class FlakyRepositoryMaterializer(TaskCompletionSource success) : IProCursorMaterializer
    {
        private int _calls;

        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            this._calls++;
            if (this._calls == 1)
            {
                throw new InvalidOperationException("transient materialization failure");
            }

            var rootDirectory = CreateRootDirectory();
            success.TrySetResult();
            return Task.FromResult(new ProCursorMaterializedSource(
                source.Id,
                trackedBranch.Id,
                trackedBranch.BranchName,
                requestedCommitSha ?? "commit-success",
                rootDirectory,
                []));
        }
    }

    private sealed class BlockingRepositoryMaterializer(
        TaskCompletionSource sourceAStarted,
        TaskCompletionSource sourceBStarted,
        TaskCompletionSource releaseSourceA) : IProCursorMaterializer
    {
        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public async Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            if (string.Equals(source.RepositoryId, "source-a", StringComparison.Ordinal))
            {
                sourceAStarted.TrySetResult();
                await releaseSourceA.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            else
            {
                sourceBStarted.TrySetResult();
            }

            return new ProCursorMaterializedSource(
                source.Id,
                trackedBranch.Id,
                trackedBranch.BranchName,
                requestedCommitSha ?? $"commit-{source.RepositoryId}",
                CreateRootDirectory(),
                []);
        }
    }

    private static string CreateRootDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "meisterpropr-procursor-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
