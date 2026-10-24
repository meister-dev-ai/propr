// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.ProCursor.Core;
using MeisterDev.ProPR.ProCursor.Options;
using MeisterDev.ProPR.ProCursor.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Tests.Workers;

public sealed class ProCursorIndexWorkerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition(), failureMessage);
    }

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
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var job = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);

        Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(
            1,
            await db.ProCursorIndexSnapshots.CountAsync(snapshot => snapshot.KnowledgeSourceId == sourceId));
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

        await SeedSourceAndJobAsync(
            provider,
            sourceAId,
            sourceABranchId,
            firstJobId,
            "source-a",
            "main",
            "text_only");
        await SeedJobAsync(provider, sourceAId, sourceABranchId, secondJobId, "2");
        await SeedSourceAndJobAsync(
            provider,
            sourceBId,
            sourceBBranchId,
            thirdJobId,
            "source-b",
            "main",
            "text_only",
            "3");

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
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var jobs = await db.ProCursorIndexJobs
            .Where(job => job.Id == firstJobId || job.Id == secondJobId || job.Id == thirdJobId)
            .OrderBy(job => job.QueuedAt)
            .ToListAsync();

        Assert.Equal(ProCursorIndexJobStatus.Completed, jobs.Single(job => job.Id == firstJobId).Status);
        Assert.Equal(ProCursorIndexJobStatus.Pending, jobs.Single(job => job.Id == secondJobId).Status);
        Assert.Equal(ProCursorIndexJobStatus.Completed, jobs.Single(job => job.Id == thirdJobId).Status);
    }

    // A job whose lease has expired has no service that runs it, for example because the ProCursor service stopped
    // while the job ran. The next worker cycle returns the job to the queue and runs it again.
    [Fact]
    public async Task Worker_RecoversAJobWhoseLeaseExpired_AndRunsItAgain()
    {
        var provider = BuildServiceProvider(
            new ImmediateRepositoryMaterializer(),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 1,
                RefreshPollSeconds = 3600,
                ChunkTargetLines = 50,
            });

        var sourceId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedSourceAndJobAsync(provider, sourceId, branchId, jobId, "source-a", "main", "text_only");
        await UpdateJobAsync(provider, jobId, job => job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await WaitForJobsAsync(provider, jobId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var recovered = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);

        Assert.Equal(2, recovered.AttemptCount);
        Assert.Null(recovered.LeaseExpiresAt);
    }

    // The tenant deletion cancels a running job in the database. The worker reads the cancellation when it renews
    // the lease, stops the job and leaves it cancelled.
    [Fact]
    public async Task Worker_StopsARunningJobThatWasCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = BuildServiceProvider(
            new CancellableRepositoryMaterializer(started, stopped),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 1,
                RefreshPollSeconds = 3600,
                ChunkTargetLines = 50,

                // The worker renews a lease of ten seconds every two seconds.
                IndexJobLeaseSeconds = 10,
            });

        var sourceId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedSourceAndJobAsync(provider, sourceId, branchId, jobId, "source-a", "main", "text_only");

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await UpdateJobAsync(provider, jobId, job => job.MarkCancelled());

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => worker.ActiveJobCount == 0,
                TimeSpan.FromSeconds(10),
                "The worker still counts the cancelled job as running.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var job = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);

        Assert.Equal(ProCursorIndexJobStatus.Cancelled, job.Status);
    }

    // When the database cannot record a renewal, the worker stops the job before its lease expires. Another service
    // can recover the job once the lease has expired, and two services then do not run the job at the same time. A
    // renewal that hangs stops the job as well as one that fails.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_StopsARunningJobWhoseLeaseCannotBeRenewed(bool renewalHangs)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = BuildServiceProvider(
            new CancellableRepositoryMaterializer(started, stopped),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 1,
                RefreshPollSeconds = 3600,
                ChunkTargetLines = 50,

                // The worker renews a lease of ten seconds every two seconds.
                IndexJobLeaseSeconds = 10,
            });

        var sourceId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedSourceAndJobAsync(provider, sourceId, branchId, jobId, "source-a", "main", "text_only");

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var outage = provider.GetRequiredService<JobWriteOutage>();
            outage.Hangs = renewalHangs;
            outage.Active = true;

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(
                () => worker.ActiveJobCount == 0,
                TimeSpan.FromSeconds(10),
                "The worker still counts the job without a lease as running.");
        }
        finally
        {
            provider.GetRequiredService<JobWriteOutage>().Active = false;
            await worker.StopAsync(CancellationToken.None);
        }

        // The job stays claimed by the first attempt, so the recovery of an expired lease can take it.
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var job = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);

        Assert.Equal(ProCursorIndexJobStatus.Processing, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.NotNull(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task Worker_WithIncompleteProCursorGraph_StartsAndCompletesCycleWithoutThrowing()
    {
        using var provider = BuildIncompleteGraphServiceProvider();
        var worker = provider.GetRequiredService<ProCursorIndexWorker>();

        await worker.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(
                () => worker.LastCycleCompletedAt is not null,
                TimeSpan.FromSeconds(1),
                "Worker did not complete a cycle for the incomplete graph scenario.");

            Assert.True(worker.IsRunning);
            Assert.NotNull(worker.LastCycleStartedAt);
            Assert.NotNull(worker.LastCycleCompletedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider BuildServiceProvider(
        IProCursorMaterializer materializer,
        ProCursorOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var databaseName = Guid.NewGuid().ToString("N");
        var operationalDatabaseName = Guid.NewGuid().ToString("N");
        services.AddDbContext<MeisterProPRDbContext>(builder => builder.UseInMemoryDatabase(databaseName));
        services.AddDbContext<ProCursorOperationalDbContext>(builder => builder.UseInMemoryDatabase(operationalDatabaseName));

        services.AddScoped<IProCursorKnowledgeSourceRepository, ProCursorKnowledgeSourceRepository>();
        services.AddScoped<ProCursorIndexJobRepository>();
        services.AddScoped<IProCursorIndexJobRepository, InMemoryIndexJobRepository>();
        services.AddSingleton<JobWriteOutage>();
        services.AddScoped<IProCursorIndexSnapshotRepository, ProCursorIndexSnapshotRepository>();
        services.AddScoped<ProCursorSymbolGraphRepository>();
        services.AddScoped<IProCursorSymbolGraphRepository>(sp =>
            sp.GetRequiredService<ProCursorSymbolGraphRepository>());

        services.AddSingleton(materializer);
        services.AddSingleton<IProCursorChunkExtractor, EmptyChunkExtractor>();
        services.AddSingleton<IProCursorEmbeddingService, EmptyEmbeddingService>();
        services.AddSingleton<IProCursorSymbolExtractor, EmptySymbolExtractor>();
        services.AddSingleton<IProCursorTrackedBranchChangeDetector, NoOpTrackedBranchChangeDetector>();
        services.AddScoped<ProCursorRefreshScheduler>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<ProCursorIndexWorker>();

        return services.BuildServiceProvider(true);
    }

    private static ServiceProvider BuildIncompleteGraphServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ProCursorRefreshScheduler>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddSingleton(
            Options.Create(
                new ProCursorOptions
                {
                    MaxIndexConcurrency = 1,
                    RefreshPollSeconds = 1,
                    ChunkTargetLines = 50,
                }));
        services.AddSingleton<ProCursorIndexWorker>();

        return services.BuildServiceProvider(true);
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
        var operationalDb = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();

        db.Clients.Add(
            new ClientRecord
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
        operationalDb.ProCursorIndexJobs.Add(
            new ProCursorIndexJob(
                jobId,
                sourceId,
                branchId,
                null,
                "refresh",
                $"{sourceId:N}:{branchId:N}:{dedupSuffix}"));
        await db.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();
    }

    private static async Task SeedJobAsync(
        ServiceProvider provider,
        Guid sourceId,
        Guid branchId,
        Guid jobId,
        string dedupSuffix)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();

        db.ProCursorIndexJobs.Add(
            new ProCursorIndexJob(
                jobId,
                sourceId,
                branchId,
                null,
                "refresh",
                $"{sourceId:N}:{branchId:N}:{dedupSuffix}"));
        await db.SaveChangesAsync();
    }

    private static async Task UpdateJobAsync(ServiceProvider provider, Guid jobId, Action<ProCursorIndexJob> change)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        var job = await db.ProCursorIndexJobs.SingleAsync(current => current.Id == jobId);
        change(job);
        await db.SaveChangesAsync();
    }

    private static async Task WaitForJobsAsync(ServiceProvider provider, params Guid[] jobIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
            var allCompleted = await db.ProCursorIndexJobs
                .Where(job => jobIds.Contains(job.Id))
                .AllAsync(job => job.Status == ProCursorIndexJobStatus.Completed);

            if (allCompleted)
            {
                return;
            }

            await Task.Delay(10, CancellationToken.None);
        }

        throw new TimeoutException("Timed out waiting for ProCursor jobs to complete.");
    }

    private static string CreateRootDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "meisterpropr-procursor-worker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
            return Task.FromResult(
                new ProCursorMaterializedSource(
                    source.Id,
                    trackedBranch.Id,
                    trackedBranch.BranchName,
                    requestedCommitSha ?? "commit-success",
                    rootDirectory,
                    []));
        }
    }

    private sealed class ImmediateRepositoryMaterializer : IProCursorMaterializer
    {
        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            return Task.FromResult(
                new ProCursorMaterializedSource(
                    source.Id,
                    trackedBranch.Id,
                    trackedBranch.BranchName,
                    requestedCommitSha ?? "commit-recovered",
                    CreateRootDirectory(),
                    []));
        }
    }

    /// <summary>
    ///     Writes the recovery of an expired lease through the change tracker. The in-memory provider does not translate
    ///     the conditional update of the repository, and the PostgreSQL repository tests cover its condition.
    /// </summary>
    /// <remarks>While <see cref="JobWriteOutage.Active" /> is set, every write of a job fails or hangs.</remarks>
    private sealed class InMemoryIndexJobRepository(ProCursorIndexJobRepository inner, JobWriteOutage outage)
        : IProCursorIndexJobRepository
    {
        public Task AddAsync(ProCursorIndexJob job, CancellationToken ct = default) => inner.AddAsync(job, ct);

        public Task<ProCursorIndexJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default) => inner.GetByIdAsync(jobId, ct);

        public Task<ProCursorIndexJob?> GetNextPendingAsync(CancellationToken ct = default) => inner.GetNextPendingAsync(ct);

        public Task<ProCursorIndexJob?> GetNextPendingAsync(IReadOnlyCollection<Guid> excludedSourceIds, CancellationToken ct = default) =>
            inner.GetNextPendingAsync(excludedSourceIds, ct);

        public Task<IReadOnlyList<ProCursorIndexJob>> ListActiveAsync(Guid knowledgeSourceId, CancellationToken ct = default) =>
            inner.ListActiveAsync(knowledgeSourceId, ct);

        public Task<bool> HasActiveJobAsync(Guid trackedBranchId, string dedupKey, CancellationToken ct = default) =>
            inner.HasActiveJobAsync(trackedBranchId, dedupKey, ct);

        public Task<IReadOnlyList<ProCursorIndexJob>> ListExpiredLeasesAsync(
            DateTimeOffset now,
            IReadOnlyCollection<Guid> excludedJobIds,
            CancellationToken ct = default) => inner.ListExpiredLeasesAsync(now, excludedJobIds, ct);

        public Task<bool> RecoverExpiredLeaseAsync(ProCursorIndexJob job, DateTimeOffset now, CancellationToken ct = default) =>
            this.UpdateAsync(job, ct);

        public async Task<bool> UpdateAsync(ProCursorIndexJob job, CancellationToken ct = default)
        {
            if (!outage.Active)
            {
                return await inner.UpdateAsync(job, ct);
            }

            if (outage.Hangs)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            throw new InvalidOperationException("The database cannot be reached.");
        }
    }

    /// <summary>Makes the writes of jobs fail or hang, as if the database could not be reached.</summary>
    private sealed class JobWriteOutage
    {
        public bool Active { get; set; }

        public bool Hangs { get; set; }
    }

    /// <summary>Waits until the execution is cancelled, and reports the start and the cancellation.</summary>
    private sealed class CancellableRepositoryMaterializer(TaskCompletionSource started, TaskCompletionSource stopped)
        : IProCursorMaterializer
    {
        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public async Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The delay ends only when the execution is cancelled.");
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
}
