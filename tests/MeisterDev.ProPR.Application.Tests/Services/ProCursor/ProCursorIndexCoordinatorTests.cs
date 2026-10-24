// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Core;
using MeisterDev.ProPR.ProCursor.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services.ProCursor;

/// <summary>
///     Unit tests for <see cref="ProCursorIndexCoordinator" /> covering durable job execution.
/// </summary>
public sealed class ProCursorIndexCoordinatorTests
{
    [Fact]
    public async Task ExecuteJobAsync_ForAClaimedJob_PersistsReadySnapshotAndCompletesJob()
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
        jobRepository.UpdateAsync(job, Arg.Any<CancellationToken>())
            .Returns(true);
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

        var claimed = await coordinator.TryStartNextJobAsync([], CancellationToken.None);
        var handled = await coordinator.ExecuteJobAsync(claimed!.Id, claimed.AttemptCount, CancellationToken.None);

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
    public async Task TryStartNextJobAsync_GivesTheClaimALeaseOfTheConfiguredDuration()
    {
        var job = CreateJob();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        jobRepository.GetNextPendingAsync(Arg.Any<CancellationToken>()).Returns(job);
        jobRepository.UpdateAsync(job, Arg.Any<CancellationToken>()).Returns(true);
        var before = DateTimeOffset.UtcNow;

        var claimed = await CreateCoordinator(jobRepository, leaseSeconds: 120).TryStartNextJobAsync([]);

        Assert.Same(job, claimed);
        Assert.Equal(ProCursorIndexJobStatus.Processing, job.Status);
        Assert.InRange(job.LeaseExpiresAt!.Value, before.AddSeconds(120), DateTimeOffset.UtcNow.AddSeconds(120));
    }

    // Another ProCursor service claimed the job between the read and the update, and this service does not run it.
    [Fact]
    public async Task TryStartNextJobAsync_WhenAnotherServiceClaimedTheJobFirst_ReturnsNull()
    {
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        jobRepository.GetNextPendingAsync(Arg.Any<CancellationToken>()).Returns(CreateJob());
        jobRepository.UpdateAsync(Arg.Any<ProCursorIndexJob>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.Null(await CreateCoordinator(jobRepository).TryStartNextJobAsync([]));
    }

    // An HTTP request that runs into its timeout throws a cancellation that the caller did not request. The attempt
    // fails, and the job returns to the queue instead of staying in processing.
    [Fact]
    public async Task ExecuteJobAsync_WhenAStepTimesOut_ReturnsTheJobToTheQueue()
    {
        var (source, branch) = CreateSource();
        var job = CreateJob(source.Id, branch.Id);
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(5));
        var (jobRepository, sourceRepository, materializer) = CreateExecution(source, job);
        materializer.MaterializeAsync(source, branch, null, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<ProCursorMaterializedSource>(
                    new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.")));

        Assert.True(await CreateCoordinator(jobRepository, sourceRepository, materializer).ExecuteJobAsync(job.Id, job.AttemptCount));

        Assert.Equal(ProCursorIndexJobStatus.Pending, job.Status);
        Assert.Contains("HttpClient.Timeout", job.FailureReason, StringComparison.Ordinal);
        await jobRepository.Received(1).UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    // The index worker cancels the execution when the job was cancelled or recovered elsewhere. The execution then
    // writes nothing to the job.
    [Fact]
    public async Task ExecuteJobAsync_WhenTheCallerCancels_LeavesTheJobToTheCanceller()
    {
        var (source, branch) = CreateSource();
        var job = CreateJob(source.Id, branch.Id);
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(5));
        var (jobRepository, sourceRepository, materializer) = CreateExecution(source, job);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        materializer.MaterializeAsync(source, branch, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<ProCursorMaterializedSource>(cancellation.Token));
        var coordinator = CreateCoordinator(jobRepository, sourceRepository, materializer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteJobAsync(job.Id, job.AttemptCount, cancellation.Token));

        Assert.Equal(ProCursorIndexJobStatus.Processing, job.Status);
        await jobRepository.DidNotReceive().UpdateAsync(Arg.Any<ProCursorIndexJob>(), Arg.Any<CancellationToken>());
    }

    // A service can pause after its claim until another service recovered the job and claimed it again. The
    // paused service then does not run the newer attempt.
    [Fact]
    public async Task ExecuteJobAsync_ForAJobClaimedAgainByAnotherService_DoesNotRunIt()
    {
        var (source, branch) = CreateSource();
        var job = CreateJob(source.Id, branch.Id);
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(-1));
        job.MarkPendingForRetry("The lease expired.");
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(5));
        var (jobRepository, sourceRepository, materializer) = CreateExecution(source, job);

        Assert.False(await CreateCoordinator(jobRepository, sourceRepository, materializer).ExecuteJobAsync(job.Id, 1));

        await materializer.DidNotReceive().MaterializeAsync(
            Arg.Any<ProCursorKnowledgeSource>(),
            Arg.Any<ProCursorTrackedBranch>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceive().UpdateAsync(Arg.Any<ProCursorIndexJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_ForAJobCancelledAfterItsClaim_DoesNotRunIt()
    {
        var (source, branch) = CreateSource();
        var job = CreateJob(source.Id, branch.Id);
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(5));
        job.MarkCancelled();
        var (jobRepository, sourceRepository, materializer) = CreateExecution(source, job);

        Assert.False(await CreateCoordinator(jobRepository, sourceRepository, materializer).ExecuteJobAsync(job.Id, job.AttemptCount));

        await materializer.DidNotReceive().MaterializeAsync(
            Arg.Any<ProCursorKnowledgeSource>(),
            Arg.Any<ProCursorTrackedBranch>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverExpiredJobsAsync_ReturnsAJobToTheQueueAndFailsAJobAfterItsLastAttempt()
    {
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var retried = CreateJob();
        retried.MarkProcessing(expiredAt);
        var exhausted = CreateJob();
        for (var attempt = 1; attempt < 3; attempt++)
        {
            exhausted.MarkProcessing(expiredAt);
            exhausted.MarkPendingForRetry("The materialization failed.");
        }

        exhausted.MarkProcessing(expiredAt);
        var runningHere = Guid.NewGuid();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        jobRepository.ListExpiredLeasesAsync(
                Arg.Any<DateTimeOffset>(),
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { runningHere })),
                Arg.Any<CancellationToken>())
            .Returns([retried, exhausted]);
        jobRepository.RecoverExpiredLeaseAsync(Arg.Any<ProCursorIndexJob>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var recovered = await CreateCoordinator(jobRepository).RecoverExpiredJobsAsync([runningHere]);

        Assert.Equal(2, recovered);
        Assert.Equal(ProCursorIndexJobStatus.Pending, retried.Status);
        Assert.Equal(ProCursorIndexJobStatus.Failed, exhausted.Status);
        Assert.Contains("lease", exhausted.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsTheLeaseOnlyForTheClaimThatTheJobIsIn()
    {
        var job = CreateJob();
        job.MarkProcessing(DateTimeOffset.UtcNow.AddSeconds(10));
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        jobRepository.UpdateAsync(job, Arg.Any<CancellationToken>()).Returns(true);
        var coordinator = CreateCoordinator(jobRepository, leaseSeconds: 300);

        Assert.True(await coordinator.RenewLeaseAsync(job.Id, 1));
        Assert.True(job.LeaseExpiresAt > DateTimeOffset.UtcNow.AddSeconds(290));

        // Another service recovered the job and claimed it again, so the first claim has ended.
        job.MarkPendingForRetry("The lease expired.");
        job.MarkProcessing(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(await coordinator.RenewLeaseAsync(job.Id, 1));

        job.MarkCancelled();
        Assert.False(await coordinator.RenewLeaseAsync(job.Id, 2));
    }

    private static ProCursorIndexJob CreateJob(Guid? sourceId = null, Guid? branchId = null)
    {
        return new ProCursorIndexJob(Guid.NewGuid(), sourceId ?? Guid.NewGuid(), branchId ?? Guid.NewGuid(), null, "refresh", "dedup-key");
    }

    private static (ProCursorKnowledgeSource Source, ProCursorTrackedBranch Branch) CreateSource()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "App Repository",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-app",
            "main",
            null,
            true,
            "auto");
        return (source, source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.Manual, true));
    }

    private static (IProCursorIndexJobRepository Jobs, IProCursorKnowledgeSourceRepository Sources, IProCursorMaterializer Materializer)
        CreateExecution(ProCursorKnowledgeSource source, ProCursorIndexJob job)
    {
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        jobRepository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        jobRepository.UpdateAsync(job, Arg.Any<CancellationToken>()).Returns(true);
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        sourceRepository.GetBySourceIdAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        var materializer = Substitute.For<IProCursorMaterializer>();
        materializer.SourceKind.Returns(ProCursorSourceKind.Repository);
        return (jobRepository, sourceRepository, materializer);
    }

    private static ProCursorIndexCoordinator CreateCoordinator(
        IProCursorIndexJobRepository jobRepository,
        IProCursorKnowledgeSourceRepository? sourceRepository = null,
        IProCursorMaterializer? materializer = null,
        int leaseSeconds = 300)
    {
        return new ProCursorIndexCoordinator(
            sourceRepository ?? Substitute.For<IProCursorKnowledgeSourceRepository>(),
            jobRepository,
            Substitute.For<IProCursorIndexSnapshotRepository>(),
            Substitute.For<IProCursorSymbolGraphRepository>(),
            materializer is null ? [] : [materializer],
            Substitute.For<IProCursorChunkExtractor>(),
            Substitute.For<IProCursorEmbeddingService>(),
            Substitute.For<IProCursorSymbolExtractor>(),
            NullLogger<ProCursorIndexCoordinator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions { IndexJobLeaseSeconds = leaseSeconds }));
    }
}
