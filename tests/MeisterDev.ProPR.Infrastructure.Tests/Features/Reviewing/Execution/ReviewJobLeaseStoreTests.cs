// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Integration tests for claiming and liveness against a real PostgreSQL instance. These have to run
///     against the real database, because the claim relies on the database rather than the process to decide
///     which caller wins, and an in-memory double would prove nothing about that.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewJobLeaseStoreTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const int ShortCeilingMinutes = 30;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private static readonly IOptions<ReviewLeaseOptions> LeaseOptions =
        Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions());

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private ReviewJobLeaseStore _store = null!;
    private JobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        this._repo = this.CreateRepository(this._dbContext);
        this._store = new ReviewJobLeaseStore(this._dbContext, this._repo, LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    // The defect this replaces: the claim used to load the row, compare its status in memory, and save.
    // Two hosts running that at the same time both saw Pending and both proceeded as the winner.
    [Fact]
    public async Task TryClaim_FromTwoHostsAtOnce_GrantsTheJobToExactlyOne()
    {
        var job = await this.AddPendingJobAsync();

        await using var contextA = new MeisterProPRDbContext(this._options);
        await using var contextB = new MeisterProPRDbContext(this._options);
        var storeA = new ReviewJobLeaseStore(contextA, this.CreateRepository(contextA), LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);
        var storeB = new ReviewJobLeaseStore(contextB, this.CreateRepository(contextB), LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);

        var grants = await Task.WhenAll(
            storeA.TryClaimAsync(job.Id, "host-a", LeaseDuration),
            storeB.TryClaimAsync(job.Id, "host-b", LeaseDuration));

        Assert.Single(grants, grant => grant is not null);
    }

    [Fact]
    public async Task TryClaim_StampsOwnerGenerationAndExpiry_InTheSameTransitionAsTheStatus()
    {
        var job = await this.AddPendingJobAsync();

        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        Assert.NotNull(lease);
        Assert.Equal("host-a", lease.Owner);
        Assert.Equal(1, lease.Generation);

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal("host-a", stored.LeaseOwner);
        Assert.Equal(1, stored.LeaseGeneration);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.NotNull(stored.LastHeartbeatAt);
        // Expiry is computed by the database, so hosts with skewed clocks still agree on when it ends.
        Assert.True(stored.LeaseExpiresAt > stored.LastHeartbeatAt);
    }

    [Fact]
    public async Task TryClaim_OnAJobThatIsNotPending_GrantsNothing()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        var second = await this._store.TryClaimAsync(job.Id, "host-b", LeaseDuration);

        Assert.Null(second);
    }

    [Fact]
    public async Task TryRenew_ByTheCurrentHolder_MovesTheExpiryForward()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        var claimedExpiry = (await this.ReadJobAsync(job.Id)).LeaseExpiresAt;

        var renewal = await this._store.TryRenewAsync(lease, TimeSpan.FromMinutes(10));

        Assert.True(renewal.Accepted);
        var renewedExpiry = (await this.ReadJobAsync(job.Id)).LeaseExpiresAt;
        Assert.True(renewedExpiry > claimedExpiry);
    }

    // The fencing case: a process paused past its expiry, reclaimed by someone else, then resumed. Its
    // generation is behind, so it must not be able to extend a lease it no longer holds.
    [Fact]
    public async Task TryRenew_WithAStaleGeneration_IsRejectedAndLeavesTheExpiryAlone()
    {
        var job = await this.AddPendingJobAsync();
        var firstLease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(firstLease);

        // Someone else takes the job over: the row goes back to pending and is claimed again, which bumps
        // the generation past the one the first holder still carries.
        await this._store.TryReleaseAsync(firstLease);
        var secondLease = await this._store.TryClaimAsync(job.Id, "host-b", LeaseDuration);
        Assert.NotNull(secondLease);
        Assert.Equal(firstLease.Generation + 1, secondLease.Generation);

        var expiryBefore = (await this.ReadJobAsync(job.Id)).LeaseExpiresAt;
        var renewal = await this._store.TryRenewAsync(firstLease, TimeSpan.FromHours(1));

        Assert.False(renewal.Accepted);
        Assert.Equal(expiryBefore, (await this.ReadJobAsync(job.Id)).LeaseExpiresAt);
    }

    [Fact]
    public async Task TryRenew_ByADifferentOwnerHoldingTheSameGeneration_IsRejected()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        var renewal = await this._store.TryRenewAsync(lease with { Owner = "host-b" }, LeaseDuration);

        Assert.False(renewal.Accepted);
    }

    [Fact]
    public async Task IsLeaseCurrent_IsFalse_ForAHolderThatWasReclaimed()
    {
        var job = await this.AddPendingJobAsync();
        var firstLease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(firstLease);
        await this._store.TryReleaseAsync(firstLease);
        var secondLease = await this._store.TryClaimAsync(job.Id, "host-b", LeaseDuration);
        Assert.NotNull(secondLease);

        Assert.False(await this._store.IsLeaseCurrentAsync(firstLease));
        Assert.True(await this._store.IsLeaseCurrentAsync(secondLease));
    }

    // A planned shutdown hands the job back rather than letting it time out, so the queue picks it up at
    // once and nothing counts the interruption against the job.
    [Fact]
    public async Task TryRelease_ReturnsTheJobToPendingAndClearsTheLease()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        Assert.True(await this._store.TryReleaseAsync(lease));

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
        // The generation stays where it is, so the releasing party cannot pass a fencing check afterwards.
        Assert.Equal(lease.Generation, stored.LeaseGeneration);
    }

    [Fact]
    public async Task TryRelease_ByAPartyThatDoesNotHoldTheLease_ChangesNothing()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        Assert.False(await this._store.TryReleaseAsync(lease with { Owner = "host-b" }));
        Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(job.Id)).Status);
    }

    // The defect this replaces: crash and expiry were bounded by the reclaim budget and deliberate failure
    // was not, so a host that failed every attempt released the lease as if healthy and re-leased its own
    // failure without limit, at full AI cost per cycle. A live run reached generation 755 this way.
    [Fact]
    public async Task TryReleaseFailed_SpendsAReclaimAttemptOnTheWayBackToThePool()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        var outcome = await this._store.TryReleaseFailedAsync(lease, maxConsecutiveReclaims: 3, maxTotalReclaims: 12);

        Assert.Equal(ReviewJobReclaimOutcome.Requeued, outcome);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Equal(1, stored.ConsecutiveReclaimCount);
        Assert.Equal(1, stored.TotalReclaimCount);
        Assert.NotNull(stored.LastReclaimedAt);
    }

    [Fact]
    public async Task TryReleaseFailed_PastTheBudget_FailsTheJobWithAReasonInsteadOfRequeueing()
    {
        var job = await this.AddPendingJobAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var earlier = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
            Assert.NotNull(earlier);
            Assert.Equal(
                ReviewJobReclaimOutcome.Requeued,
                await this._store.TryReleaseFailedAsync(earlier, maxConsecutiveReclaims: 3, maxTotalReclaims: 12));
        }

        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);
        var final = await this._store.TryReleaseFailedAsync(lease, maxConsecutiveReclaims: 3, maxTotalReclaims: 12);

        Assert.Equal(ReviewJobReclaimOutcome.FailedOutOfReclaimBudget, final);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal(ReviewJobFailureReason.LeaseLost, stored.FailureReason);
        Assert.NotNull(stored.ErrorMessage);
    }

    [Fact]
    public async Task TryReleaseFailed_ByAPartyThatDoesNotHoldTheLease_CountsNothing()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        var outcome = await this._store.TryReleaseFailedAsync(lease with { Owner = "host-b" }, maxConsecutiveReclaims: 3, maxTotalReclaims: 12);

        Assert.Equal(ReviewJobReclaimOutcome.NotReclaimed, outcome);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal(0, stored.ConsecutiveReclaimCount);
    }

    // A drain must stay free. If a planned shutdown started spending reclaim attempts, rolling a fleet
    // three times would fail every job it happened to be holding.
    [Fact]
    public async Task TryRelease_StillCountsNothing()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        Assert.True(await this._store.TryReleaseAsync(lease));

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(0, stored.ConsecutiveReclaimCount);
        Assert.Equal(0, stored.TotalReclaimCount);
    }

    [Fact]
    public async Task GetClaimCandidates_IsBoundedByTheLimitAndOldestFirst()
    {
        var older = await this.AddPendingJobAsync(prId: 1);
        await Task.Delay(10);
        await this.AddPendingJobAsync(prId: 2);

        var candidates = await this._store.GetClaimCandidatesAsync(1);

        Assert.Single(candidates);
        Assert.Equal(older.Id, candidates[0].Id);
    }

    [Fact]
    public async Task GetClaimCandidates_ExcludesJobsThatAreAlreadyLeased()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        Assert.Empty(await this._store.GetClaimCandidatesAsync(10));
    }

    // The cursor is what lets a caller whose whole window was ineligible page deeper instead of starving
    // whatever sits behind it.
    [Fact]
    public async Task GetClaimCandidates_PagesDeeperFromACursor()
    {
        var first = await this.AddPendingJobAsync(prId: 1);
        await Task.Delay(10);
        await this.AddPendingJobAsync(prId: 2);
        await Task.Delay(10);
        var third = await this.AddPendingJobAsync(prId: 3);

        var window = await this._store.GetClaimCandidatesAsync(2);
        Assert.Equal(2, window.Count);
        Assert.Equal(first.Id, window[0].Id);

        var nextWindow = await this._store.GetClaimCandidatesAsync(2, window[^1].SubmittedAt);

        Assert.Equal([third.Id], nextWindow.Select(job => job.Id));
    }

    // Nothing should still look leased once the job is over, or an operator reading the registry sees a
    // holder for work that finished hours ago.
    [Fact]
    public async Task ReachingATerminalState_ClearsTheLease()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        await this._repo.SetFailedAsync(job.Id, "boom");

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
        Assert.Null(stored.LastHeartbeatAt);
    }

    // The conditional transition is the same mechanism the claim uses, and the same defect applied to it:
    // two hosts both moving one job out of Pending.
    [Fact]
    public async Task TryTransition_FromTwoHostsAtOnce_SucceedsForExactlyOne()
    {
        var job = await this.AddPendingJobAsync();

        await using var contextA = new MeisterProPRDbContext(this._options);
        await using var contextB = new MeisterProPRDbContext(this._options);
        var repoA = this.CreateRepository(contextA);
        var repoB = this.CreateRepository(contextB);

        var results = await Task.WhenAll(
            repoA.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing),
            repoB.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing));

        Assert.Single(results, won => won);
    }

    // Reclaim is what replaces failing a job for its age. It is driven by an expired lease, so a review
    // that is simply long is invisible to it.
    [Fact]
    public async Task GetExpiredLeases_FindsOnlyLeasesThatHaveActuallyExpired()
    {
        var live = await this.AddPendingJobAsync(prId: 1);
        var abandoned = await this.AddPendingJobAsync(prId: 2);
        await this._store.TryClaimAsync(live.Id, "host-a", TimeSpan.FromMinutes(30));
        await this._store.TryClaimAsync(abandoned.Id, "host-b", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var expired = await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30));

        Assert.Single(expired);
        Assert.Equal(abandoned.Id, expired[0].JobId);
    }

    [Fact]
    public async Task TryReclaim_ReturnsAnAbandonedJobToThePendingPool()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var expired = await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30));

        var outcome = await this._store.TryReclaimAsync(expired[0], 3, 12);

        Assert.Equal(ReviewJobReclaimOutcome.Requeued, outcome);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Equal(1, stored.ConsecutiveReclaimCount);
        Assert.Equal(1, stored.TotalReclaimCount);
        Assert.NotNull(stored.LastReclaimedAt);
    }

    // Several hosts sweep on their own schedules, so they will meet on the same expired job.
    [Fact]
    public async Task TryReclaim_FromTwoHostsAtOnce_TakesTheJobBackExactlyOnce()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];

        await using var contextA = new MeisterProPRDbContext(this._options);
        await using var contextB = new MeisterProPRDbContext(this._options);
        var storeA = new ReviewJobLeaseStore(contextA, this.CreateRepository(contextA), LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);
        var storeB = new ReviewJobLeaseStore(contextB, this.CreateRepository(contextB), LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);

        var outcomes = await Task.WhenAll(
            storeA.TryReclaimAsync(expired, 3, 12),
            storeB.TryReclaimAsync(expired, 3, 12));

        Assert.Single(outcomes, outcome => outcome != ReviewJobReclaimOutcome.NotReclaimed);
        Assert.Equal(1, (await this.ReadJobAsync(job.Id)).TotalReclaimCount);
    }

    // A holder that was merely slow, and recovered in time to renew, keeps its job: the generation it holds
    // is still current and the sweep's view of it is stale.
    [Fact]
    public async Task TryReclaim_DoesNothing_WhenTheHolderRenewedAfterTheScan()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        Assert.NotNull(lease);
        await Task.Delay(50);
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];

        await this._store.TryRenewAsync(lease, TimeSpan.FromMinutes(30));
        var outcome = await this._store.TryReclaimAsync(expired, 3, 12);

        Assert.Equal(ReviewJobReclaimOutcome.NotReclaimed, outcome);
        Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(job.Id)).Status);
    }

    // Automatic reclaim replaced a deliberate operator restart, so it needs a bound of its own: a job that
    // dies the same way every time would otherwise cycle at full AI cost forever.
    [Fact]
    public async Task TryReclaim_FailsTheJob_OnceTheConsecutiveBudgetIsSpent()
    {
        var job = await this.AddPendingJobAsync();

        ReviewJobReclaimOutcome outcome = ReviewJobReclaimOutcome.NotReclaimed;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
            await Task.Delay(30);
            var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];
            outcome = await this._store.TryReclaimAsync(expired, 2, 12);
        }

        Assert.Equal(ReviewJobReclaimOutcome.FailedOutOfReclaimBudget, outcome);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal(ReviewJobFailureReason.LeaseLost, stored.FailureReason);
        Assert.NotNull(stored.ErrorMessage);
    }

    // A deploy is not evidence that a job is poisonous, so handing the lease back deliberately must not
    // spend any of the budget that exists to stop a genuinely broken job cycling.
    [Fact]
    public async Task TryRelease_ConsumesNoReclaimAttempt()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        await this._store.TryReleaseAsync(lease);

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(0, stored.ConsecutiveReclaimCount);
        Assert.Equal(0, stored.TotalReclaimCount);
        Assert.Null(stored.LastReclaimedAt);
    }

    // Reclaiming a job while its comments are going out is how one review gets posted twice.
    [Fact]
    public async Task AJobThatIsPublishing_IsNotOfferedForReclaim()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await this._store.TryMarkPublishingAsync(job.Id);
        await Task.Delay(50);

        var expired = await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30));

        Assert.Empty(expired);
    }

    [Fact]
    public async Task APublicationThatOutlivesItsTimeout_IsFailedDistinctlyRatherThanReclaimed()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        await this._store.TryMarkPublishingAsync(job.Id);
        await Task.Delay(50);

        var failed = await this._store.FailTimedOutPublicationsAsync(10, TimeSpan.FromMilliseconds(1));

        Assert.Equal([job.Id], failed);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal(ReviewJobFailureReason.PublicationTimedOut, stored.FailureReason);
    }

    [Fact]
    public async Task ClearPublishing_MakesTheJobReclaimableAgain()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await this._store.TryMarkPublishingAsync(job.Id);
        await this._store.ClearPublishingAsync(job.Id);
        await Task.Delay(50);

        Assert.Single(await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)));
    }

    // A reclaim ends the earlier attempt entirely. Left stamped, the previous attempt's publication mark
    // had the timeout sweep terminally fail the NEXT attempt seconds in, for a publication that never
    // happened on it.
    [Fact]
    public async Task TryReclaim_ClearsTheEarlierAttemptsPublicationStamp()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await this._store.TryMarkPublishingAsync(job.Id);
        await Task.Delay(50);
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)))[0];

        var outcome = await this._store.TryReclaimAsync(expired, 3, 12);

        Assert.Equal(ReviewJobReclaimOutcome.Requeued, outcome);
        Assert.Null((await this.ReadJobAsync(job.Id)).PublishingStartedAt);
    }

    [Fact]
    public async Task TryReleaseFailed_ClearsTheEarlierAttemptsPublicationStamp()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);
        await this._store.TryMarkPublishingAsync(job.Id);

        await this._store.TryReleaseFailedAsync(lease, maxConsecutiveReclaims: 3, maxTotalReclaims: 12);

        Assert.Null((await this.ReadJobAsync(job.Id)).PublishingStartedAt);
    }

    // Belt and braces for rows written before the requeue paths cleared the stamp: a claim starts a fresh
    // attempt, and a fresh attempt has not begun publishing.
    [Fact]
    public async Task TryClaim_StartsCleanOfAnyLeftoverPublicationStamp()
    {
        var job = await this.AddPendingJobAsync();
        await this._dbContext.ReviewJobs
            .Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PublishingStartedAt, DateTimeOffset.UtcNow));

        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        Assert.NotNull(lease);
        Assert.Null((await this.ReadJobAsync(job.Id)).PublishingStartedAt);
    }

    // The requeue transition callers outside the lease subsystem use. The pool has to get the job back
    // clean, because lease columns left stamped make a Pending job read as held by an attempt that is over.
    [Fact]
    public async Task TryTransition_RequeueReturnsTheJobClean()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);

        Assert.True(await this._repo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending));

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
        Assert.Null(stored.LastHeartbeatAt);
    }

    // Never while comments are going out, whoever asks: requeuing a publishing job is how the same review
    // gets posted twice.
    [Fact]
    public async Task TryTransition_RefusesToRequeueAPublishingJob()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        await this._store.TryMarkPublishingAsync(job.Id);

        Assert.False(await this._repo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending));
        Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(job.Id)).Status);
    }

    // After a control-plane outage every lease expires at once. Without a backoff the fleet would take the
    // whole queue back, immediately, over and over.
    [Fact]
    public async Task AJobReclaimedRecently_IsLeftAloneUntilTheBackoffPasses()
    {
        var job = await this.AddPendingJobAsync();
        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];
        await this._store.TryReclaimAsync(expired, 3, 12);

        await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);

        Assert.Empty(await this._store.GetExpiredLeasesAsync(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task GetExpiredLeases_IsBoundedByTheSweepLimit()
    {
        for (var i = 1; i <= 3; i++)
        {
            var job = await this.AddPendingJobAsync(prId: i);
            await this._store.TryClaimAsync(job.Id, "host-a", TimeSpan.FromMilliseconds(1));
        }

        await Task.Delay(50);

        Assert.Equal(2, (await this._store.GetExpiredLeasesAsync(2, TimeSpan.Zero, TimeSpan.FromMinutes(30))).Count);
    }

    // The heartbeat is the only channel that reaches an execution wherever it runs. An operator stop issued
    // on one host has to arrive at the host actually doing the work, and arrive as a stop rather than as an
    // unexplained refusal, or the job is finalised as a generic failure.
    [Theory]
    [InlineData("stopped", ReviewJobStopReason.OperatorStop)]
    [InlineData("superseded", ReviewJobStopReason.Superseded)]
    [InlineData("budget", ReviewJobStopReason.BudgetCapReached)]
    [InlineData("cancelled", ReviewJobStopReason.OperatorStop)]
    public async Task TryRenew_CarriesTheReasonTheJobWasHalted(string halt, ReviewJobStopReason expected)
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        switch (halt)
        {
            case "stopped":
                await this._repo.SetStoppedAsync(job.Id);
                break;
            case "superseded":
                await this._repo.SetSupersededAsync(job.Id);
                break;
            case "budget":
                await this._repo.SetBudgetExceededAsync(job.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, 10m, 11m);
                break;
            default:
                await this._repo.SetCancelledAsync(job.Id);
                break;
        }

        var renewal = await this._store.TryRenewAsync(lease, LeaseDuration);

        Assert.False(renewal.Accepted);
        Assert.Equal(ReviewJobDirective.Stop, renewal.Directive);
        Assert.Equal(expected, renewal.StopReason);
    }

    // Losing the job to someone else is not the same as the job being halted: the reason has to say which,
    // because only one of them means the outcome is this party's to report.
    [Fact]
    public async Task TryRenew_ReportsALostLeaseSeparatelyFromAHaltedJob()
    {
        var job = await this.AddPendingJobAsync();
        var firstLease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(firstLease);
        await this._store.TryReleaseAsync(firstLease);
        await this._store.TryClaimAsync(job.Id, "host-b", LeaseDuration);

        var renewal = await this._store.TryRenewAsync(firstLease, LeaseDuration);

        Assert.False(renewal.Accepted);
        Assert.Equal(ReviewJobStopReason.LeaseNoLongerHeld, renewal.StopReason);
    }

    [Fact]
    public async Task TryRenew_TellsAHealthyHolderToCarryOn()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        var renewal = await this._store.TryRenewAsync(lease, LeaseDuration);

        Assert.True(renewal.Accepted);
        Assert.Equal(ReviewJobDirective.Continue, renewal.Directive);
        Assert.Equal(ReviewJobStopReason.None, renewal.StopReason);
    }

    /// <summary>
    ///     A holder that is still renewing its lease and has not finished the review. The execution continues
    ///     only while its renewals succeed, so the renewal is refused once the ceiling is passed and the job
    ///     is failed with that reason. If the lease were left to expire, the expiry would be treated as an
    ///     abandonment and the job would be reclaimed and run again.
    /// </summary>
    [Fact]
    public async Task TryRenew_ForAJobPastTheDurationCeiling_StopsItAndRecordsWhy()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        await this.BackdateProcessingStartAsync(job.Id, TimeSpan.FromMinutes(ShortCeilingMinutes + 1));
        var store = new ReviewJobLeaseStore(
            this._dbContext,
            this._repo,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions { MaxReviewDurationMinutes = ShortCeilingMinutes }),
            NullLogger<ReviewJobLeaseStore>.Instance);

        var renewal = await store.TryRenewAsync(lease!, LeaseDuration);

        Assert.False(renewal.Accepted);
        Assert.Equal(ReviewJobDirective.Stop, renewal.Directive);
        Assert.Equal(ReviewJobStopReason.MaxDurationExceeded, renewal.StopReason);

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Contains($"{ShortCeilingMinutes}-minute", stored.ErrorMessage!, StringComparison.Ordinal);

        // The classification, not only the message: it is what separates a job stopped for its duration from
        // one that was interrupted, and the other terminal paths in this store record their own.
        Assert.Equal(ReviewJobFailureReason.MaxDurationExceeded, stored.FailureReason);
    }

    [Fact]
    public async Task TryRenew_ForAJobInsideTheDurationCeiling_KeepsRenewing()
    {
        var job = await this.AddPendingJobAsync();
        var lease = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        await this.BackdateProcessingStartAsync(job.Id, TimeSpan.FromMinutes(ShortCeilingMinutes - 2));
        var store = new ReviewJobLeaseStore(
            this._dbContext,
            this._repo,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions { MaxReviewDurationMinutes = ShortCeilingMinutes }),
            NullLogger<ReviewJobLeaseStore>.Instance);

        var renewal = await store.TryRenewAsync(lease!, LeaseDuration);

        Assert.True(renewal.Accepted);
        Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(job.Id)).Status);
    }

    private async Task BackdateProcessingStartAsync(Guid jobId, TimeSpan by)
    {
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE review_jobs SET processing_started_at = now() - make_interval(secs => {1}) WHERE id = {0}",
            [jobId, by.TotalSeconds]);
    }

    private JobRepository CreateRepository(MeisterProPRDbContext context)
    {
        return new JobRepository(
            context,
            new TestDbContextFactory(this._options),
            NullLogger<JobRepository>.Instance);
    }

    private async Task<ReviewJob> AddPendingJobAsync(int prId = 1)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            prId,
            1);
        await this._repo.AddAsync(job);
        return job;
    }

    private async Task<ReviewJob> ReadJobAsync(Guid id)
    {
        await using var context = new MeisterProPRDbContext(this._options);
        return await context.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }
}
