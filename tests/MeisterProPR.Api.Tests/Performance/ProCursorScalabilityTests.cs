// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
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

namespace MeisterProPR.Api.Tests.Performance;

public sealed class ProCursorScalabilityTests
{
    [Fact]
    public async Task TrackedBranchRefresh_P95Completion_StaysWithinSc004Budget()
    {
        var expectedCommitsByBranchId = new Dictionary<Guid, string>();
        var provider = BuildServiceProvider(
            new ImmediateRepositoryMaterializer(),
            new DeterministicChangeDetector(expectedCommitsByBranchId),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 4,
                RefreshPollSeconds = 1,
                ChunkTargetLines = 50,
            });

        var clientId = Guid.NewGuid();
        await SeedClientAsync(provider, clientId, "ProCursor Refresh SLA");

        for (var index = 1; index <= 10; index++)
        {
            var sourceId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            expectedCommitsByBranchId[branchId] = $"commit-{index:00}-new";

            await SeedTrackedSourceAsync(
                provider,
                clientId,
                sourceId,
                branchId,
                $"repo-{index:00}",
                ProCursorRefreshTriggerMode.BranchUpdate,
                $"commit-{index:00}-old");
        }

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        IReadOnlyCollection<TimeSpan> completionTimes;
        try
        {
            completionTimes = await WaitForIndexedBranchesAsync(provider, expectedCommitsByBranchId, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(expectedCommitsByBranchId.Count, completionTimes.Count);
        var p95 = CalculateP95(completionTimes);
        Assert.True(
            p95 < TimeSpan.FromMinutes(15),
            $"Expected tracked-branch refresh p95 latency to stay below 15 minutes for SC-004, but observed {p95}.");

        await using var verificationScope = provider.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.Equal(expectedCommitsByBranchId.Count, await verificationDb.ProCursorIndexSnapshots.CountAsync());
    }

    [Fact]
    public async Task OneSourceFailure_DoesNotCascadeBeyondSc005Budget()
    {
        const int sourceCount = 20;
        const string failingRepositoryId = "repo-20";

        var provider = BuildServiceProvider(
            new SelectiveRepositoryMaterializer(failingRepositoryId),
            new NoOpTrackedBranchChangeDetector(),
            new ProCursorOptions
            {
                MaxIndexConcurrency = 6,
                RefreshPollSeconds = 1,
                ChunkTargetLines = 50,
            });

        var clientId = Guid.NewGuid();
        await SeedClientAsync(provider, clientId, "ProCursor Isolation SLA");

        var jobIds = new List<Guid>(sourceCount);
        var failingJobId = Guid.Empty;

        for (var index = 1; index <= sourceCount; index++)
        {
            var sourceId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var repositoryId = $"repo-{index:00}";

            if (string.Equals(repositoryId, failingRepositoryId, StringComparison.Ordinal))
            {
                failingJobId = jobId;
            }

            jobIds.Add(jobId);
            await SeedTrackedSourceAndJobAsync(provider, clientId, sourceId, branchId, jobId, repositoryId, ProCursorRefreshTriggerMode.Manual);
        }

        var worker = provider.GetRequiredService<ProCursorIndexWorker>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await WaitForTerminalJobsAsync(provider, jobIds, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var jobs = await db.ProCursorIndexJobs
            .Where(job => jobIds.Contains(job.Id))
            .ToListAsync();

        var unrelatedJobs = jobs.Where(job => job.Id != failingJobId).ToList();
        Assert.NotEmpty(unrelatedJobs);
        var unrelatedFailureRate = unrelatedJobs.Count(job => job.Status != ProCursorIndexJobStatus.Completed) / (double)unrelatedJobs.Count;

        Assert.Equal(ProCursorIndexJobStatus.Failed, jobs.Single(job => job.Id == failingJobId).Status);
        Assert.All(unrelatedJobs, job => Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status));
        Assert.True(
            unrelatedFailureRate < 0.01d,
            $"Expected unrelated-source unsuccessful refreshes to remain below 1% for SC-005, but observed {unrelatedFailureRate:P2}.");
    }

    private static ServiceProvider BuildServiceProvider(
        IProCursorMaterializer materializer,
        IProCursorTrackedBranchChangeDetector changeDetector,
        ProCursorOptions options)
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
        services.AddSingleton<IProCursorTrackedBranchChangeDetector>(changeDetector);
        services.AddSingleton<IProCursorChunkExtractor, EmptyChunkExtractor>();
        services.AddSingleton<IProCursorEmbeddingService, EmptyEmbeddingService>();
        services.AddSingleton<IProCursorSymbolExtractor, EmptySymbolExtractor>();

        services.AddScoped<ProCursorRefreshScheduler>();
        services.AddScoped<ProCursorIndexCoordinator>();
        services.AddSingleton<IOptions<ProCursorOptions>>(Options.Create(options));
        services.AddSingleton<ProCursorIndexWorker>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedClientAsync(ServiceProvider provider, Guid clientId, string displayName)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.Clients.Add(new ClientRecord
        {
            Id = clientId,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTrackedSourceAsync(
        ServiceProvider provider,
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        string repositoryId,
        ProCursorRefreshTriggerMode refreshTriggerMode,
        string initialCommit)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        var source = new ProCursorKnowledgeSource(
            sourceId,
            clientId,
            repositoryId,
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
            "main",
            null,
            true,
            "text_only");

        var branch = source.AddTrackedBranch(branchId, "main", refreshTriggerMode, true);
        branch.RecordSeenCommit(initialCommit);
        branch.RecordIndexedCommit(initialCommit);

        db.ProCursorKnowledgeSources.Add(source);
        await db.SaveChangesAsync();
    }

    private static async Task SeedTrackedSourceAndJobAsync(
        ServiceProvider provider,
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        Guid jobId,
        string repositoryId,
        ProCursorRefreshTriggerMode refreshTriggerMode)
    {
        await SeedTrackedSourceAsync(provider, clientId, sourceId, branchId, repositoryId, refreshTriggerMode, "commit-old");

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.ProCursorIndexJobs.Add(new ProCursorIndexJob(
            jobId,
            sourceId,
            branchId,
            null,
            "refresh",
            $"{sourceId:N}:{branchId:N}:refresh:head"));
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyCollection<TimeSpan>> WaitForIndexedBranchesAsync(
        ServiceProvider provider,
        IReadOnlyDictionary<Guid, string> expectedCommitsByBranchId,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var completionTimes = new Dictionary<Guid, TimeSpan>();

        while (stopwatch.Elapsed < timeout)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var branches = await db.ProCursorTrackedBranches
                .Where(branch => expectedCommitsByBranchId.Keys.Contains(branch.Id))
                .ToListAsync();

            foreach (var branch in branches)
            {
                if (completionTimes.ContainsKey(branch.Id))
                {
                    continue;
                }

                if (expectedCommitsByBranchId.TryGetValue(branch.Id, out var expectedCommit) &&
                    string.Equals(branch.LastIndexedCommitSha, expectedCommit, StringComparison.Ordinal))
                {
                    completionTimes[branch.Id] = stopwatch.Elapsed;
                }
            }

            if (completionTimes.Count == expectedCommitsByBranchId.Count)
            {
                return completionTimes.Values.ToArray();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for ProCursor tracked branches to refresh.");
    }

    private static async Task WaitForTerminalJobsAsync(
        ServiceProvider provider,
        IReadOnlyCollection<Guid> jobIds,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var jobs = await db.ProCursorIndexJobs
                .Where(job => jobIds.Contains(job.Id))
                .ToListAsync();

            if (jobs.Count == jobIds.Count &&
                jobs.All(job => job.Status is ProCursorIndexJobStatus.Completed or ProCursorIndexJobStatus.Failed or ProCursorIndexJobStatus.Cancelled or ProCursorIndexJobStatus.Superseded))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for ProCursor jobs to reach terminal states.");
    }

    private static TimeSpan CalculateP95(IReadOnlyCollection<TimeSpan> samples)
    {
        if (samples.Count == 0)
        {
            throw new InvalidOperationException("Cannot calculate p95 for an empty sample set.");
        }

        var ordered = samples.OrderBy(sample => sample).ToArray();
        var p95Index = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.95d) - 1);
        return ordered[p95Index];
    }

    private sealed class DeterministicChangeDetector(IReadOnlyDictionary<Guid, string> latestCommitsByBranchId)
        : IProCursorTrackedBranchChangeDetector
    {
        public Task<string?> GetLatestCommitShaAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            CancellationToken ct = default)
        {
            return Task.FromResult(latestCommitsByBranchId.TryGetValue(trackedBranch.Id, out var commit)
                ? commit
                : trackedBranch.LastSeenCommitSha);
        }
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

    private sealed class ImmediateRepositoryMaterializer : IProCursorMaterializer
    {
        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            var rootDirectory = CreateRootDirectory();
            return Task.FromResult(new ProCursorMaterializedSource(
                source.Id,
                trackedBranch.Id,
                trackedBranch.BranchName,
                requestedCommitSha ?? trackedBranch.LastSeenCommitSha ?? "commit-head",
                rootDirectory,
                []));
        }
    }

    private sealed class SelectiveRepositoryMaterializer(string failingRepositoryId) : IProCursorMaterializer
    {
        public ProCursorSourceKind SourceKind => ProCursorSourceKind.Repository;

        public Task<ProCursorMaterializedSource> MaterializeAsync(
            ProCursorKnowledgeSource source,
            ProCursorTrackedBranch trackedBranch,
            string? requestedCommitSha,
            CancellationToken ct = default)
        {
            if (string.Equals(source.RepositoryId, failingRepositoryId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated source-specific failure");
            }

            var rootDirectory = CreateRootDirectory();
            return Task.FromResult(new ProCursorMaterializedSource(
                source.Id,
                trackedBranch.Id,
                trackedBranch.BranchName,
                requestedCommitSha ?? trackedBranch.LastSeenCommitSha ?? "commit-head",
                rootDirectory,
                []));
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

    private static string CreateRootDirectory()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "meisterpropr-procursor-performance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        return rootDirectory;
    }
}
