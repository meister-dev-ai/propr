// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

/// <summary>
///     The two catch-up sweeps: roll-up cells for findings collected before the projection existed, and
///     measurements for pull requests whose closure the synchronization path never saw.
/// </summary>
public sealed class CodeInsightCatchUpTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid ClientB = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTimeOffset ReviewedAt = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;
    private readonly CodeInsightRollupProjector _projector;
    private readonly CodeInsightRollupReader _reader;
    private readonly CodeInsightSealSweeper _sweeper;
    private readonly ICodeInsightsCollectionGate _gate;
    private readonly ICodeInsightMetricSealer _sealer;
    private readonly IPullRequestFetcher _pullRequests;
    private readonly IJobRepository _jobs;

    public CodeInsightCatchUpTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightCatchUpTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());

        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this._projector = new CodeInsightRollupProjector(
            this._dbContext,
            this._gate,
            NullLogger<CodeInsightRollupProjector>.Instance);
        this._reader = new CodeInsightRollupReader(this._dbContext);

        this._sealer = Substitute.For<ICodeInsightMetricSealer>();
        this._sealer
            .SealAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        this._pullRequests = Substitute.For<IPullRequestFetcher>();
        this.WithProviderStatus(PrStatus.Completed);

        this._jobs = Substitute.For<IJobRepository>();
        this._jobs.GetById(Arg.Any<Guid>()).Returns(call => new ReviewJob(
            (Guid)call[0],
            ClientA,
            "https://dev.azure.com/org",
            "project",
            "repo-1",
            7,
            1));

        this._sweeper = new CodeInsightSealSweeper(
            this._dbContext,
            this._sealer,
            this._gate,
            this._jobs,
            NullLogger<CodeInsightSealSweeper>.Instance,
            this._pullRequests);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task TheBackfillProjectsFindingsThatHaveNoCellsAtAll()
    {
        // Everything collected before the projection existed is invisible in every view until this runs.
        await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs"]);

        Assert.Equal(0, await this._reader.GetTotalAsync(this.Window(ClientA)));

        Assert.Equal(1, await this._projector.BackfillAsync(10));

        Assert.Equal(2, await this._reader.GetTotalAsync(this.Window(ClientA)));
    }

    [Fact]
    public async Task TheBackfillLeavesAlreadyProjectedJobsAlone()
    {
        var jobId = await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._projector.ProjectJobAsync(jobId);

        Assert.Equal(0, await this._projector.BackfillAsync(10));
        Assert.Equal(1, await this._reader.GetTotalAsync(this.Window(ClientA)));
    }

    [Fact]
    public async Task TheBackfillIsBoundedAndResumes()
    {
        await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 1, ["a.cs"]);
        await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 2, ["b.cs"]);
        await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 3, ["c.cs"]);

        Assert.Equal(2, await this._projector.BackfillAsync(2));
        // The candidates are whatever is still missing, so the next sweep simply finds less to do.
        Assert.Equal(1, await this._projector.BackfillAsync(2));
        Assert.Equal(0, await this._projector.BackfillAsync(2));
        Assert.Equal(3, await this._reader.GetTotalAsync(this.Window(ClientA)));
    }

    [Fact]
    public async Task AnOptedOutClientsBacklogCannotOccupyTheBatch()
    {
        // Applied while selecting, not after: an opted-out client's findings can never be projected, so letting
        // them into the batch would starve the clients that can make progress.
        await this.SeedFindingsOnlyAsync(ClientB, "repo-1", 1, ["closed.cs"]);
        await this.SeedFindingsOnlyAsync(ClientA, "repo-1", 2, ["open.cs"]);
        this._gate.IsCollectionEnabledAsync(ClientB, Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(1, await this._projector.BackfillAsync(1));

        Assert.Equal(1, await this._reader.GetTotalAsync(this.Window(ClientA)));
        Assert.Equal(0, await this._reader.GetTotalAsync(this.Window(ClientB)));
    }

    [Fact]
    public async Task TheSealSweepSealsAQuietPullRequestTheProviderReportsAsFinished()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        this.WithProviderStatus(PrStatus.Completed);

        Assert.Equal(1, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._sealer.Received(1).SealAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == ClientA && key.RepositoryId == "repo-1" && key.PullRequestId == 7),
            "Completed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAbandonedPullRequestSealsWithItsOwnState()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        this.WithProviderStatus(PrStatus.Abandoned);

        Assert.Equal(1, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._sealer.Received(1).SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            "Abandoned",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStillOpenPullRequestIsLeftAlone()
    {
        // The provider also answers Active for a transient failure, which is the safe answer: a measurement
        // postponed is recoverable, one sealed against a wrong status is not.
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        this.WithProviderStatus(PrStatus.Active);

        Assert.Equal(0, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._sealer.DidNotReceive().SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APullRequestStillSeeingActivityIsNotAskedAbout()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 1);

        Assert.Equal(0, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._pullRequests.DidNotReceive().FetchRefAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyMeasuredPullRequestIsNotAskedAboutAgain()
    {
        var aggregateId = await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        await this.WriteSealAsync(aggregateId, ClientA, "repo-1", 7);

        Assert.Equal(0, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._pullRequests.DidNotReceive().FetchRefAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOptedOutClientIsNotAskedAboutEither()
    {
        await this.SeedQuietAsync(ClientB, "repo-1", 7, idleDays: 30);
        this._gate.IsCollectionEnabledAsync(ClientB, Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(0, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._pullRequests.DidNotReceive().FetchRefAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneUnreachablePullRequestDoesNotEndTheSweepForTheRest()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        await this.SeedQuietAsync(ClientA, "repo-1", 8, idleDays: 29);

        var calls = 0;
        this._pullRequests
            .FetchRefAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new HttpRequestException("the provider is unreachable")
                : Task.FromResult(new PullRequestRef("feature", "main", PrStatus.Completed)));

        Assert.Equal(1, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task WithoutAProviderAdapterTheSweepDoesNothingRatherThanFailing()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);

        var offline = new CodeInsightSealSweeper(
            this._dbContext,
            this._sealer,
            this._gate,
            this._jobs,
            NullLogger<CodeInsightSealSweeper>.Instance);

        Assert.Equal(0, await offline.SweepAsync(10, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task APullRequestWhoseReviewJobIsGoneIsLeftUnmeasuredRatherThanGuessedAt()
    {
        await this.SeedQuietAsync(ClientA, "repo-1", 7, idleDays: 30);
        this._jobs.GetById(Arg.Any<Guid>()).Returns((ReviewJob?)null);

        Assert.Equal(0, await this._sweeper.SweepAsync(10, TimeSpan.FromDays(7)));

        await this._sealer.DidNotReceive().SealAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSweepTakesTheMostRecentlyActivePullRequestsFirst()
    {
        // A pull request that closed last week is worth far more to a current metric than one quiet for a year.
        await this.SeedQuietAsync(ClientA, "repo-1", 100, idleDays: 300);
        await this.SeedQuietAsync(ClientA, "repo-1", 200, idleDays: 8);

        Assert.Equal(1, await this._sweeper.SweepAsync(1, TimeSpan.FromDays(7)));

        await this._sealer.Received(1).SealAsync(
            Arg.Is<CodeInsightPullRequestKey>(key => key.PullRequestId == 200),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private void WithProviderStatus(PrStatus status)
    {
        this._pullRequests
            .FetchRefAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PullRequestRef("feature", "main", status));
    }

    private CodeInsightRollupQuery Window(params Guid[] clientIds)
    {
        return new CodeInsightRollupQuery(clientIds, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
    }

    /// <summary>Materialises findings without projecting them, as a pre-projection collection would have.</summary>
    private async Task<Guid> SeedFindingsOnlyAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string[] files)
    {
        var jobId = Guid.NewGuid();
        var key = new CodeInsightPullRequestKey(clientId, repositoryId, pullRequestId);
        var snapshots = files
            .Select((file, ordinal) => new CodeInsightFindingSnapshot(
                ordinal,
                file,
                10 + ordinal,
                CommentSeverity.Error,
                $"Finding {ordinal} in {file}",
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                null,
                $"thread-{jobId:N}-{ordinal}",
                $"comment-{jobId:N}-{ordinal}"))
            .ToList();

        await this._store.MaterialiseFindingsAsync(key, jobId, $"rev-{jobId:N}", ReviewedAt, snapshots);
        return jobId;
    }

    /// <summary>Seeds a collected pull request whose last activity is the given number of days ago.</summary>
    private async Task<Guid> SeedQuietAsync(Guid clientId, string repositoryId, long pullRequestId, int idleDays)
    {
        await this.SeedFindingsOnlyAsync(clientId, repositoryId, pullRequestId, ["a.cs"]);

        var aggregate = await this._dbContext.CodeInsightPullRequests
            .SingleAsync(candidate => candidate.ClientId == clientId
                                      && candidate.RepositoryId == repositoryId
                                      && candidate.PullRequestId == pullRequestId);
        aggregate.LastActivityAt = DateTimeOffset.UtcNow.AddDays(-idleDays);
        await this._dbContext.SaveChangesAsync();
        return aggregate.Id;
    }

    private async Task WriteSealAsync(Guid aggregateId, Guid clientId, string repositoryId, long pullRequestId)
    {
        this._dbContext.CodeInsightPullRequestMetrics.Add(
            new CodeInsightPullRequestMetric
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = aggregateId,
                ClientId = clientId,
                RepositoryId = repositoryId,
                PullRequestId = pullRequestId,
                AddressedCount = 1,
                ResolvedCount = 1,
                Precision = 1d,
                CloseState = "Completed",
                SealedAt = DateTimeOffset.UtcNow,
                SealedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });
        await this._dbContext.SaveChangesAsync();
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightCatchUpTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
