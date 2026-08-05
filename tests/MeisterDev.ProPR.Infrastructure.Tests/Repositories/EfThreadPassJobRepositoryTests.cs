// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Threads.Persistence;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     The claims a thread pass depends on are held in the database, so two crawl configurations and two
///     deployed instances cannot both answer one pull request.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfThreadPassJobRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid SeedClientId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string ScopePath = "https://dev.azure.com/org";
    private const string ProjectKey = "project";
    private const string RepositoryId = "repo-thread-pass";
    private const int PullRequestId = 4242;

    private readonly List<MeisterProPRDbContext> _contexts = [];
    private MeisterProPRDbContext _dbContext = null!;
    private EfThreadPassJobRepository _repository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = this.CreateDbContext();

        if (!await this._dbContext.Clients.AnyAsync(client => client.Id == SeedClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = SeedClientId,
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "Thread Pass Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        await this._dbContext.ThreadPassHandledThreads.ExecuteDeleteAsync();
        await this._dbContext.ThreadPassJobs.ExecuteDeleteAsync();
        this._repository = new EfThreadPassJobRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        foreach (var context in this._contexts)
        {
            await context.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryClaimAsync_WhilePassIsInFlight_RefusesTheSecondPass()
    {
        var first = await this._repository.TryClaimAsync(CreatePass("7|aaa"));
        var second = await this._repository.TryClaimAsync(CreatePass("8|bbb"));

        Assert.True(first.WasClaimed);
        Assert.False(second.WasClaimed);
        Assert.NotNull(second.BlockingJob);
    }

    [Fact]
    public async Task TryClaimAsync_ContenderAlreadyCommitted_RefusesWithoutAskingTheDatabaseToRefuse()
    {
        // A pass stays in flight for as long as it takes to answer, and every crawl tick in between arrives
        // here. Losing by exception writes a failed command and a failed save at error level on each of those
        // ticks, which is how a pass that is merely running comes to read as a database fault. A contender that
        // is already committed can be seen, so it is answered without an insert the database has to reject.
        await this._repository.TryClaimAsync(CreatePass("7|aaa"));

        var secondWriterContext = this.CreateDbContext();
        var saveFailures = 0;
        secondWriterContext.SaveChangesFailed += (_, _) => saveFailures++;
        var secondWriter = new EfThreadPassJobRepository(secondWriterContext);

        var second = await secondWriter.TryClaimAsync(CreatePass("8|bbb"));

        Assert.False(second.WasClaimed);
        Assert.NotNull(second.BlockingJob);
        Assert.Equal(0, saveFailures);
    }

    [Fact]
    public async Task TryClaimAsync_TriggerStateAlreadyRun_RefusesToRepeatTheWork()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);
        await this._repository.SetCompletedAsync(job.Id);

        var repeat = await this._repository.TryClaimAsync(CreatePass("7|aaa"));

        Assert.False(repeat.WasClaimed);
        Assert.Equal(job.Id, repeat.BlockingJob?.Id);
    }

    [Fact]
    public async Task TryClaimAsync_TriggerStateMoved_QueuesTheNextPass()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);
        await this._repository.SetCompletedAsync(job.Id);

        var next = await this._repository.TryClaimAsync(CreatePass("8|bbb"));

        Assert.True(next.WasClaimed);
    }

    [Fact]
    public async Task RecordAttemptFailureAsync_ExhaustsItsAttempts_ThenStopsRetrying()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);

        for (var attempt = 1; attempt <= ThreadPassJob.MaxAttempts; attempt++)
        {
            Assert.True(await this._repository.TryBeginAttemptAsync(job.Id));
            var attemptsRemain = await this._repository.RecordAttemptFailureAsync(job.Id, "the provider refused");
            Assert.Equal(attempt < ThreadPassJob.MaxAttempts, attemptsRemain);

            // Stands in for waiting out the retry delay, which is what spaces the attempts in production.
            await this.ClearRetryDelayAsync(job.Id);
        }

        Assert.False(await this._repository.TryBeginAttemptAsync(job.Id));
        Assert.Empty(await this._repository.GetPendingAsync(10));

        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Failed, stored.Status);
        Assert.Equal(ThreadPassJob.MaxAttempts, stored.AttemptCount);
    }

    [Fact]
    public async Task RecordHandledThreadAsync_SameThreadTwiceAtOneRevision_IsRecordedOnce()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);

        await this._repository.RecordHandledThreadAsync(job.Id, SeedClientId, RepositoryId, PullRequestId, "17", 2, "7");
        await this._repository.RecordHandledThreadAsync(job.Id, SeedClientId, RepositoryId, PullRequestId, "17", 2, "7");
        await this._repository.RecordHandledThreadAsync(job.Id, SeedClientId, RepositoryId, PullRequestId, "17", 3, "7");

        var handled = await this._repository.GetHandledThreadKeysAsync(SeedClientId, RepositoryId, PullRequestId, "7");
        Assert.Equal(2, handled.Count);
    }

    [Fact]
    public async Task GetHandledThreadKeysAsync_ThreadJudgedAtAnEarlierRevision_DoesNotSuppressTheNextOne()
    {
        // The headline promise: push a fix and the finding is judged again. A finding nobody replied to keeps
        // an observed count of zero forever, so only the revision distinguishes the two units of work.
        var earlier = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(earlier);
        await this._repository.RecordHandledThreadAsync(
            earlier.Id,
            SeedClientId,
            RepositoryId,
            PullRequestId,
            "17",
            0,
            "7");
        await this._repository.TryBeginAttemptAsync(earlier.Id);
        await this._repository.SetCompletedAsync(earlier.Id);

        var atNewRevision = await this._repository.GetHandledThreadKeysAsync(
            SeedClientId,
            RepositoryId,
            PullRequestId,
            "8");

        Assert.Empty(atNewRevision);
        Assert.Single(await this._repository.GetHandledThreadKeysAsync(SeedClientId, RepositoryId, PullRequestId, "7"));
    }

    [Fact]
    public async Task TryClaimAsync_TwoWritersWithDifferentTriggerStatesAtOnce_OnlyOneWins()
    {
        // Two crawl configurations over one repository, or two instances, arrive a second apart with
        // different trigger states. Nothing downstream separates them, so the database has to.
        var firstWriter = new EfThreadPassJobRepository(this.CreateDbContext());
        var secondWriter = new EfThreadPassJobRepository(this.CreateDbContext());

        var results = await Task.WhenAll(
            Task.Run(() => firstWriter.TryClaimAsync(CreatePass("7|aaa"))),
            Task.Run(() => secondWriter.TryClaimAsync(CreatePass("8|bbb"))));

        Assert.Single(results, result => result.WasClaimed);
        Assert.Equal(
            1,
            await this.CreateDbContext().ThreadPassJobs.CountAsync(candidate => candidate.ClientId == SeedClientId
                                                                                && candidate.RepositoryId == RepositoryId
                                                                                && candidate.PullRequestId == PullRequestId));
    }

    [Fact]
    public async Task TryClaimAsync_EarlierPassDidNothing_LetsTheIdenticalTriggerRunAgain()
    {
        // Re-enabling thread interaction must not be a silent no-op: the pass that was shut out reached a
        // terminal status having touched nothing, so it speaks for no work and blocks nothing.
        var shutOut = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(shutOut);
        await this._repository.TryBeginAttemptAsync(shutOut.Id);
        await this._repository.SetSkippedAsync(shutOut.Id, "Thread interaction was switched off.");

        var retry = await this._repository.TryClaimAsync(CreatePass("7|aaa"));

        Assert.True(retry.WasClaimed);
    }

    [Fact]
    public async Task CancelActiveForPullRequestAsync_CancelsInFlightPassesOnly()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);

        var cancelled = await this._repository.CancelActiveForPullRequestAsync(
            SeedClientId,
            RepositoryId,
            PullRequestId);

        Assert.Equal(1, cancelled);
        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Cancelled, stored.Status);
        Assert.Equal(0, await this._repository.CancelActiveForPullRequestAsync(SeedClientId, RepositoryId, PullRequestId));
    }

    [Fact]
    public async Task ReclaimStalledAsync_ReturnsAnAbandonedPassToPendingWithoutRefundingItsAttempt()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);

        await this._dbContext.ThreadPassJobs
            .Where(candidate => candidate.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.ProcessingStartedAt,
                (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-2)));

        var sweep = await this._repository.ReclaimStalledAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, sweep.ReturnedToPending);
        Assert.Equal(0, sweep.Exhausted);
        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Pending, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ReclaimStalledAsync_PassAbandonedOnItsLastAttempt_FailsItInsteadOfBlockingThePullRequest()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);

        for (var attempt = 1; attempt < ThreadPassJob.MaxAttempts; attempt++)
        {
            await this._repository.TryBeginAttemptAsync(job.Id);
            await this._repository.RecordAttemptFailureAsync(job.Id, "the provider refused");
            await this.ClearRetryDelayAsync(job.Id);
        }

        await this._repository.TryBeginAttemptAsync(job.Id);
        await this.MarkStalledAsync(job.Id);

        var sweep = await this._repository.ReclaimStalledAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(0, sweep.ReturnedToPending);
        Assert.Equal(1, sweep.Exhausted);
        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Failed, stored.Status);

        // Terminal means the next trigger state gets its own pass rather than losing the claim to a row no
        // worker will ever dispatch.
        Assert.True((await this._repository.TryClaimAsync(CreatePass("8|bbb"))).WasClaimed);
    }

    [Fact]
    public async Task SetCompletedAsync_PassCancelledMidFlight_LeavesItCancelled()
    {
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);
        await this._repository.SetCancelledAsync(job.Id);

        Assert.False(await this._repository.SetCompletedAsync(job.Id));
        Assert.False(await this._repository.RecordAttemptFailureAsync(job.Id, "the provider refused"));

        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Cancelled, stored.Status);
    }

    [Fact]
    public async Task TryBeginAttemptAsync_WithinTheRetryDelay_SpendsNoAttempt()
    {
        // A worker holding an offer made before the last attempt failed must not spend the next attempt the
        // instant it consumes it; that is how a brief provider outage burned all three in seconds.
        var job = CreatePass("7|aaa");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);
        await this._repository.RecordAttemptFailureAsync(job.Id, "the provider refused");

        Assert.False(await this._repository.TryBeginAttemptAsync(job.Id));
        Assert.Empty(await this._repository.GetPendingAsync(10));

        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task SetBudgetHeldAsync_QueuedPass_HoldsItAndRecordsWhichCapStoppedIt()
    {
        var job = CreatePass("7|held");
        await this._repository.TryClaimAsync(job);

        await this._repository.SetBudgetHeldAsync(job.Id, BudgetScopeKind.Increment, BudgetCapKind.Soft, 5m, 6m);

        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.BudgetHeld, stored.Status);
        Assert.Equal(BudgetScopeKind.Increment, stored.BudgetBlockScope);
        Assert.Equal(BudgetCapKind.Soft, stored.BudgetBlockCapKind);
        Assert.Equal(5m, stored.BudgetBlockThresholdUsd);
        Assert.Equal(6m, stored.BudgetBlockSpentUsd);
        Assert.Equal(0, stored.AttemptCount);
    }

    [Fact]
    public async Task SetBudgetHeldAsync_PassAlreadyRunning_LeavesItAlone()
    {
        var job = CreatePass("7|running");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);

        await this._repository.SetBudgetHeldAsync(job.Id, BudgetScopeKind.Increment, BudgetCapKind.Soft, 5m, 6m);

        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Processing, stored.Status);
    }

    [Fact]
    public async Task TryRestartAsync_HeldPass_ReturnsItToPendingWithItsAttemptsBack()
    {
        var job = CreatePass("7|restart");
        await this._repository.TryClaimAsync(job);
        await this._repository.TryBeginAttemptAsync(job.Id);
        await this._repository.SetBudgetExceededAsync(job.Id, BudgetScopeKind.PullRequest, BudgetCapKind.Hard, 5m, 6m);

        var restarted = await this._repository.TryRestartAsync(job.Id);

        Assert.True(restarted);
        var stored = await this.CreateDbContext().ThreadPassJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id);
        Assert.Equal(ThreadPassJobStatus.Pending, stored.Status);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.BudgetBlockScope);
        Assert.Null(stored.CompletedAt);

        // Recovered work is picked up by the scan worker exactly as a fresh pass is.
        var pending = await this._repository.GetPendingAsync(10);
        Assert.Contains(pending, candidate => candidate.Id == job.Id);
    }

    [Fact]
    public async Task TryRestartAsync_CompletedPass_RefusesToRunItAgain()
    {
        var job = CreatePass("7|completed");
        await this._repository.TryClaimAsync(job);
        await this._repository.SetCompletedAsync(job.Id);

        Assert.False(await this._repository.TryRestartAsync(job.Id));
    }

    [Fact]
    public async Task GetForPullRequestAsync_ReturnsEveryPassOverThePullRequestWithItsHandledThreads()
    {
        var first = CreatePass("7|one");
        await this._repository.TryClaimAsync(first);
        await this._repository.TryBeginAttemptAsync(first.Id);
        await this._repository.RecordHandledThreadAsync(
            first.Id,
            SeedClientId,
            RepositoryId,
            PullRequestId,
            "17",
            1,
            "7");
        await this._repository.SetCompletedAsync(first.Id);

        var second = CreatePass("8|two");
        await this._repository.TryClaimAsync(second);

        var passes = await this._repository.GetForPullRequestAsync(SeedClientId, RepositoryId, PullRequestId, 10);

        Assert.Equal(2, passes.Count);
        Assert.Single(passes.First(pass => pass.Id == first.Id).HandledThreads);
    }

    private static ThreadPassJob CreatePass(string triggerKey)
    {
        return new ThreadPassJob(
            Guid.NewGuid(),
            SeedClientId,
            ScopePath,
            ProjectKey,
            RepositoryId,
            PullRequestId,
            7,
            triggerKey.Split('|')[0],
            triggerKey);
    }

    private async Task ClearRetryDelayAsync(Guid jobId)
    {
        await this._dbContext.ThreadPassJobs
            .Where(candidate => candidate.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.NextAttemptAt,
                (DateTimeOffset?)null));
    }

    private async Task MarkStalledAsync(Guid jobId)
    {
        await this._dbContext.ThreadPassJobs
            .Where(candidate => candidate.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.ProcessingStartedAt,
                (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-2)));
    }

    private MeisterProPRDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        var context = new MeisterProPRDbContext(options);
        this._contexts.Add(context);
        return context;
    }
}
