// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

public sealed class ProCursorRefreshSchedulerTests
{
    private static readonly Guid ClientId = Guid.Parse("60000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ScheduleRefreshesAsync_WhenTrackedBranchHeadAdvances_UpdatesLastSeenAndQueuesRefresh()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var changeDetector = Substitute.For<IProCursorTrackedBranchChangeDetector>();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var chunkExtractor = Substitute.For<IProCursorChunkExtractor>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();
        var source = CreateSource(ProCursorRefreshTriggerMode.BranchUpdate);
        var branch = source.TrackedBranches.Single();
        branch.RecordSeenCommit("commit-old");
        branch.RecordIndexedCommit("commit-old");

        sourceRepository.ListEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        sourceRepository.GetByIdAsync(ClientId, source.Id, Arg.Any<CancellationToken>())
            .Returns(source);
        jobRepository.HasActiveJobAsync(branch.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        embeddingService.EnsureConfigurationAsync(source.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        changeDetector.GetLatestCommitShaAsync(source, branch, Arg.Any<CancellationToken>())
            .Returns("commit-new");

        var coordinator = CreateCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            symbolGraphRepository,
            chunkExtractor,
            embeddingService,
            symbolExtractor);
        var scheduler = new ProCursorRefreshScheduler(
            sourceRepository,
            changeDetector,
            coordinator,
            NullLogger<ProCursorRefreshScheduler>.Instance);

        var queuedCount = await scheduler.ScheduleRefreshesAsync(CancellationToken.None);

        Assert.Equal(1, queuedCount);
        Assert.Equal("commit-new", branch.LastSeenCommitSha);
        await sourceRepository.Received(1).UpdateAsync(source, Arg.Any<CancellationToken>());
        await jobRepository.Received(1)
            .AddAsync(
                Arg.Is<ProCursorIndexJob>(job =>
                    job.KnowledgeSourceId == source.Id &&
                    job.TrackedBranchId == branch.Id &&
                    job.RequestedCommitSha == "commit-new"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleRefreshesAsync_WhenTrackedBranchHasNoNewCommit_DoesNotQueueRefresh()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var changeDetector = Substitute.For<IProCursorTrackedBranchChangeDetector>();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var chunkExtractor = Substitute.For<IProCursorChunkExtractor>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();
        var source = CreateSource(ProCursorRefreshTriggerMode.BranchUpdate);
        var branch = source.TrackedBranches.Single();
        branch.RecordSeenCommit("commit-same");
        branch.RecordIndexedCommit("commit-same");

        sourceRepository.ListEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        embeddingService.EnsureConfigurationAsync(source.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        changeDetector.GetLatestCommitShaAsync(source, branch, Arg.Any<CancellationToken>())
            .Returns("commit-same");

        var coordinator = CreateCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            symbolGraphRepository,
            chunkExtractor,
            embeddingService,
            symbolExtractor);
        var scheduler = new ProCursorRefreshScheduler(
            sourceRepository,
            changeDetector,
            coordinator,
            NullLogger<ProCursorRefreshScheduler>.Instance);

        var queuedCount = await scheduler.ScheduleRefreshesAsync(CancellationToken.None);

        Assert.Equal(0, queuedCount);
        await sourceRepository.DidNotReceive()
            .UpdateAsync(Arg.Any<ProCursorKnowledgeSource>(), Arg.Any<CancellationToken>());
        await jobRepository.DidNotReceive().AddAsync(Arg.Any<ProCursorIndexJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleRefreshesAsync_WhenCommitWasSeenButNeverIndexed_QueuesRetry()
    {
        var sourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        var changeDetector = Substitute.For<IProCursorTrackedBranchChangeDetector>();
        var jobRepository = Substitute.For<IProCursorIndexJobRepository>();
        var snapshotRepository = Substitute.For<IProCursorIndexSnapshotRepository>();
        var symbolGraphRepository = Substitute.For<IProCursorSymbolGraphRepository>();
        var chunkExtractor = Substitute.For<IProCursorChunkExtractor>();
        var embeddingService = Substitute.For<IProCursorEmbeddingService>();
        var symbolExtractor = Substitute.For<IProCursorSymbolExtractor>();
        var source = CreateSource(ProCursorRefreshTriggerMode.BranchUpdate);
        var branch = source.TrackedBranches.Single();
        branch.RecordSeenCommit("commit-stuck");

        sourceRepository.ListEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([source]));
        sourceRepository.GetByIdAsync(ClientId, source.Id, Arg.Any<CancellationToken>())
            .Returns(source);
        jobRepository.HasActiveJobAsync(branch.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        embeddingService.EnsureConfigurationAsync(source.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        changeDetector.GetLatestCommitShaAsync(source, branch, Arg.Any<CancellationToken>())
            .Returns("commit-stuck");

        var coordinator = CreateCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            symbolGraphRepository,
            chunkExtractor,
            embeddingService,
            symbolExtractor);
        var scheduler = new ProCursorRefreshScheduler(
            sourceRepository,
            changeDetector,
            coordinator,
            NullLogger<ProCursorRefreshScheduler>.Instance);

        var queuedCount = await scheduler.ScheduleRefreshesAsync(CancellationToken.None);

        Assert.Equal(1, queuedCount);
        Assert.Equal("commit-stuck", branch.LastSeenCommitSha);
        Assert.Null(branch.LastIndexedCommitSha);
        await sourceRepository.DidNotReceive().UpdateAsync(source, Arg.Any<CancellationToken>());
        await jobRepository.Received(1)
            .AddAsync(
                Arg.Is<ProCursorIndexJob>(job =>
                    job.KnowledgeSourceId == source.Id &&
                    job.TrackedBranchId == branch.Id &&
                    job.RequestedCommitSha == "commit-stuck"),
                Arg.Any<CancellationToken>());
    }

    private static ProCursorIndexCoordinator CreateCoordinator(
        IProCursorKnowledgeSourceRepository sourceRepository,
        IProCursorIndexJobRepository jobRepository,
        IProCursorIndexSnapshotRepository snapshotRepository,
        IProCursorSymbolGraphRepository symbolGraphRepository,
        IProCursorChunkExtractor chunkExtractor,
        IProCursorEmbeddingService embeddingService,
        IProCursorSymbolExtractor symbolExtractor)
    {
        return new ProCursorIndexCoordinator(
            sourceRepository,
            jobRepository,
            snapshotRepository,
            symbolGraphRepository,
            [],
            chunkExtractor,
            embeddingService,
            symbolExtractor,
            NullLogger<ProCursorIndexCoordinator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ProCursorOptions()));
    }

    private static ProCursorKnowledgeSource CreateSource(ProCursorRefreshTriggerMode refreshTriggerMode)
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
        source.AddTrackedBranch(Guid.NewGuid(), "main", refreshTriggerMode, true);
        return source;
    }
}
