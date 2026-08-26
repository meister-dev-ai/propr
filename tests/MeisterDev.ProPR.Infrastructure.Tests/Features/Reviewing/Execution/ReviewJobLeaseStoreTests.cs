// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

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
    private static readonly Guid MentionClientId = Guid.Parse("6d000000-0000-0000-0000-000000000001");

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

    // The number of reviews executing rises and falls through the day, so what can be compared against the
    // licensed ceiling is the day's highest. It is observed where a claim raises the number, because a
    // reading taken later would describe the moment it was taken.
    [Fact]
    public async Task AClaim_RecordsTheDaysConcurrentReviewPeak()
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        await this._dbContext.LicensingConcurrentReviewPeak
            .Where(row => row.PeakDate == today)
            .ExecuteDeleteAsync();

        var store = new ReviewJobLeaseStore(
            this._dbContext,
            this._repo,
            LeaseOptions,
            NullLogger<ReviewJobLeaseStore>.Instance,
            new ConcurrentReviewPeakRepository(this._dbContext));

        var first = await this.AddPendingJobAsync();
        var second = await this.AddPendingJobAsync();

        await store.TryClaimWithinProcessingCapAsync(first.Id, "host-a", LeaseDuration, 5);
        await store.TryClaimWithinProcessingCapAsync(second.Id, "host-b", LeaseDuration, 5);

        var recorded = await this._dbContext.LicensingConcurrentReviewPeak
            .AsNoTracking()
            .SingleAsync(row => row.PeakDate == today);

        Assert.Equal(2, recorded.PeakCount);
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

    // The defect this covers: the concurrency ceiling was enforced by reading a count and claiming
    // afterwards. Two replicas claiming two different jobs both read a count taken before either claim, and
    // row locking does not arbitrate them because the rows differ, so both passed a ceiling of one.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TryClaimWithinProcessingCap_FromManyHostsOnDistinctJobs_AdmitsExactlyTheCap(int cap)
    {
        const int claimants = 6;
        var jobs = new List<ReviewJob>(claimants);
        for (var i = 1; i <= claimants; i++)
        {
            jobs.Add(await this.AddPendingJobAsync(prId: i));
        }

        var contexts = Enumerable.Range(0, claimants)
            .Select(_ => new MeisterProPRDbContext(this._options))
            .ToList();

        try
        {
            var claims = await Task.WhenAll(
                jobs.Select((job, index) =>
                {
                    var store = new ReviewJobLeaseStore(
                        contexts[index],
                        this.CreateRepository(contexts[index]),
                        LeaseOptions,
                        NullLogger<ReviewJobLeaseStore>.Instance);
                    return store.TryClaimWithinProcessingCapAsync(job.Id, $"host-{index}", LeaseDuration, cap);
                }));

            Assert.Equal(cap, claims.Count(claim => claim.Outcome == ReviewJobCappedClaimOutcome.Granted));
            Assert.Equal(cap, await this._dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing));

            // Every refusal names the ceiling it was measured against, and the count it observed is at the
            // ceiling: a claimant that observed fewer would have been granted the job.
            Assert.All(
                claims.Where(claim => claim.Outcome == ReviewJobCappedClaimOutcome.AtCapacity),
                claim =>
                {
                    Assert.Equal(cap, claim.Cap);
                    Assert.True(claim.ProcessingCount is null || claim.ProcessingCount >= cap);
                });
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    // A job claimed under the cap has to be indistinguishable afterwards from one claimed without it, or the
    // heartbeat, the reclaim sweep and the publication timeout would all treat it differently.
    [Fact]
    public async Task TryClaimWithinProcessingCap_StampsWhatTheUncappedClaimStamps()
    {
        var job = await this.AddPendingJobAsync();

        var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-a", LeaseDuration, 1);

        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        Assert.NotNull(claim.Lease);
        Assert.Equal("host-a", claim.Lease.Owner);
        Assert.Equal(1, claim.Lease.Generation);

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal("host-a", stored.LeaseOwner);
        Assert.Equal(1, stored.LeaseGeneration);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.NotNull(stored.LastHeartbeatAt);
        Assert.NotNull(stored.ProcessingStartedAt);
        Assert.Null(stored.PublishingStartedAt);
        Assert.True(stored.LeaseExpiresAt > stored.LastHeartbeatAt);
    }

    // The two refusals steer a queue scan differently, so they cannot be reported as one: at the ceiling
    // nothing else is claimable either, whereas a job someone else took leaves the rest of the queue open.
    // The refusal carries both numbers, so a caller can name the ceiling and what it observed.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TryClaimWithinProcessingCap_AtTheCap_SaysSoRatherThanReportingTheJobUnclaimable(int cap)
    {
        for (var prId = 1; prId <= cap; prId++)
        {
            var running = await this.AddPendingJobAsync(prId: prId);
            Assert.NotNull(await this._store.TryClaimAsync(running.Id, $"host-{prId}", LeaseDuration));
        }

        var queued = await this.AddPendingJobAsync(prId: cap + 1);

        var claim = await this._store.TryClaimWithinProcessingCapAsync(queued.Id, "host-b", LeaseDuration, cap);

        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, claim.Outcome);
        Assert.Null(claim.Lease);
        Assert.Equal(cap, claim.Cap);
        Assert.Equal(cap, claim.ProcessingCount);
        Assert.Equal(JobStatus.Pending, (await this.ReadJobAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task TryClaimWithinProcessingCap_BelowTheCapOnAJobAlreadyTaken_SaysTheJobIsNotClaimable()
    {
        var job = await this.AddPendingJobAsync();
        Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-b", LeaseDuration, 2);

        Assert.Equal(ReviewJobCappedClaimOutcome.NotClaimable, claim.Outcome);
        Assert.Null(claim.Cap);
        Assert.Null(claim.ProcessingCount);
        Assert.Equal("host-a", (await this.ReadJobAsync(job.Id)).LeaseOwner);
    }

    // A mention reply is not a review, and the two are stored in separate tables, so a mention reply that is
    // running consumes no review capacity. A ceiling of one therefore still admits a review while a mention
    // reply is being answered.
    [Fact]
    public async Task TryClaimWithinProcessingCap_WhileAMentionReplyIsProcessing_GrantsTheReview()
    {
        var job = await this.AddPendingJobAsync();
        var mention = await this.AddProcessingMentionReplyJobAsync();

        try
        {
            var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-a", LeaseDuration, 1);

            Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
            Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(job.Id)).Status);
        }
        finally
        {
            await this.RemoveMentionReplyJobAsync(mention.Id);
        }
    }

    // A party that stalls while holding the admission lock must not block every other claimant for as long
    // as it lasts, because each of those waits occupies a pooled connection. The wait is bounded, and the
    // timeout is reported as capacity so the caller stops scanning instead of contending again immediately.
    [Fact]
    public async Task TryClaimWithinProcessingCap_WhenTheAdmissionLockIsHeldElsewhere_TimesOutAsCapacity()
    {
        var job = await this.AddPendingJobAsync();

        // Session-scoped, so it is held until this connection closes, and it contends with the
        // transaction-scoped lock the claim takes because both live in the same advisory lock space.
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using (var hold = blocker.CreateCommand())
        {
            hold.CommandText = "SELECT pg_advisory_lock(hashtextextended('propr:quota:concurrent-reviews', 0))";
            await hold.ExecuteNonQueryAsync();
        }

        try
        {
            var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-a", LeaseDuration, 1);

            Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, claim.Outcome);
            Assert.Null(claim.Lease);
            Assert.Equal(1, claim.Cap);

            // No count is carried: this claimant never entered the admission that counts executing jobs.
            Assert.Null(claim.ProcessingCount);
            Assert.Equal(JobStatus.Pending, (await this.ReadJobAsync(job.Id)).Status);
        }
        finally
        {
            // Released here rather than by closing the connection. Npgsql sends its session reset with the
            // next command on the pooled connection, so a connection that goes back to the pool unused keeps
            // the lock and the next test times out on it.
            await using var release = blocker.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock_all()";
            await release.ExecuteNonQueryAsync();
        }
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

    // Capacity is counted over the executing status, so an expired lease on its own returns nothing. The
    // reclaim sweep moves the row out of Processing, and every host runs that sweep on its own schedule,
    // without an operator step.
    [Fact]
    public async Task CapacityHeldByALostHolder_ComesBackAtExpiryAndReclaim()
    {
        var lost = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        await this.ClaimWithExpiredLeaseAsync(lost.Id, "host-a");

        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);

        var expired = await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30));
        Assert.Single(expired);
        Assert.Equal(ReviewJobReclaimOutcome.Requeued, await this._store.TryReclaimAsync(expired[0], 3, 12));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        Assert.Equal(1, await this._dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing));
    }

    // Renewal moves the expiry forward, so the sweep's view of the lease is out of date and the reclaim does
    // not apply. The row stays in Processing, so the holder keeps its unit.
    [Fact]
    public async Task CapacityHeldByASlowHolderThatRenews_StaysHeld()
    {
        var running = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        var lease = await this.ClaimWithExpiredLeaseAsync(running.Id, "host-a");
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];

        Assert.True((await this._store.TryRenewAsync(lease, TimeSpan.FromMinutes(30))).Accepted);
        Assert.Equal(ReviewJobReclaimOutcome.NotReclaimed, await this._store.TryReclaimAsync(expired, 3, 12));

        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
        Assert.Equal(JobStatus.Processing, (await this.ReadJobAsync(running.Id)).Status);
    }

    // Each reclaim moves one row out of Processing, so the number of units returned equals the number of
    // leases reclaimed. Several holders lost at once are reclaimed one at a time.
    [Fact]
    public async Task Reclaim_ReturnsOneUnitPerReclaimedHolder()
    {
        const int cap = 3;
        const int reclaimed = 2;
        for (var prId = 1; prId <= cap; prId++)
        {
            var lost = await this.AddPendingJobAsync(prId: prId);
            await this.ClaimWithExpiredLeaseAsync(lost.Id, $"host-{prId}");
        }

        var waiting = new List<ReviewJob>();
        for (var prId = 11; prId <= 13; prId++)
        {
            waiting.Add(await this.AddPendingJobAsync(prId: prId));
        }

        var expired = await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30));
        Assert.Equal(cap, expired.Count);
        for (var index = 0; index < reclaimed; index++)
        {
            Assert.Equal(ReviewJobReclaimOutcome.Requeued, await this._store.TryReclaimAsync(expired[index], 3, 12));
        }

        var outcomes = new List<ReviewJobCappedClaimOutcome>();
        for (var index = 0; index < waiting.Count; index++)
        {
            var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting[index].Id, $"host-w{index}", LeaseDuration, cap);
            outcomes.Add(claim.Outcome);
        }

        Assert.Equal(reclaimed, outcomes.Count(outcome => outcome == ReviewJobCappedClaimOutcome.Granted));
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, outcomes[^1]);
        Assert.Equal(cap, await this._dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing));
    }

    // The second reclaim carries the same expired handle, and the row it names no longer has an expired
    // lease, so the reclaim of one lost holder returns one capacity unit.
    [Fact]
    public async Task ReclaimingTheSameLostLeaseTwice_ReturnsCapacityOnce()
    {
        var lost = await this.AddPendingJobAsync(prId: 1);
        var first = await this.AddPendingJobAsync(prId: 2);
        var second = await this.AddPendingJobAsync(prId: 3);
        await this.ClaimWithExpiredLeaseAsync(lost.Id, "host-a");
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];

        Assert.Equal(ReviewJobReclaimOutcome.Requeued, await this._store.TryReclaimAsync(expired, 3, 12));
        Assert.Equal(ReviewJobReclaimOutcome.NotReclaimed, await this._store.TryReclaimAsync(expired, 3, 12));

        Assert.Equal(
            ReviewJobCappedClaimOutcome.Granted,
            (await this._store.TryClaimWithinProcessingCapAsync(first.Id, "host-b", LeaseDuration, 1)).Outcome);
        var refused = await this._store.TryClaimWithinProcessingCapAsync(second.Id, "host-c", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
        Assert.Equal(1, (await this.ReadJobAsync(lost.Id)).TotalReclaimCount);
    }

    // The lost holder resumes after its row was reclaimed and taken over by another host. Its generation is
    // behind, so neither release form applies and the unit stays with the current holder.
    [Fact]
    public async Task AStaleHoldersReleaseAfterAReclaim_ReturnsNoFurtherCapacity()
    {
        var job = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        var stale = await this.ClaimWithExpiredLeaseAsync(job.Id, "host-a");
        var expired = (await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)))[0];
        Assert.Equal(ReviewJobReclaimOutcome.Requeued, await this._store.TryReclaimAsync(expired, 3, 12));

        var current = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, current.Outcome);
        Assert.NotNull(current.Lease);
        Assert.Equal(stale.Generation + 1, current.Lease.Generation);

        Assert.False(await this._store.TryReleaseAsync(stale));
        Assert.Equal(
            ReviewJobReclaimOutcome.NotReclaimed,
            await this._store.TryReleaseFailedAsync(stale, maxConsecutiveReclaims: 3, maxTotalReclaims: 12));

        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-c", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal("host-b", stored.LeaseOwner);
    }

    // The count is over the executing status, so a job that reaches a terminal state stops holding its unit,
    // whichever terminal path it took. Every writer that finalises a job is covered, because each one moves
    // the status separately.
    [Theory]
    [InlineData("failed")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    [InlineData("stopped")]
    [InlineData("budget")]
    public async Task AJobThatReachesATerminalState_HoldsNoCapacity(string terminal)
    {
        var running = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        Assert.Equal(
            ReviewJobCappedClaimOutcome.Granted,
            (await this._store.TryClaimWithinProcessingCapAsync(running.Id, "host-a", LeaseDuration, 1)).Outcome);
        Assert.Equal(
            ReviewJobCappedClaimOutcome.AtCapacity,
            (await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1)).Outcome);

        switch (terminal)
        {
            case "failed":
                await this._repo.SetFailedAsync(running.Id, "boom");
                break;
            case "completed":
                await this._repo.SetResultAsync(running.Id, new ReviewResult("summary", []));
                break;
            case "cancelled":
                await this._repo.SetCancelledAsync(running.Id);
                break;
            case "superseded":
                await this._repo.SetSupersededAsync(running.Id);
                break;
            case "stopped":
                await this._repo.SetStoppedAsync(running.Id);
                break;
            default:
                await this._repo.SetBudgetExceededAsync(running.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, 10m, 11m);
                break;
        }

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        var stored = await this.ReadJobAsync(running.Id);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
        Assert.Equal(1, await this._dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing));
    }

    // A publishing job is excluded from the reclaim sweep, so no reclaim returns its unit while comments are
    // going out. The unit returns when the job leaves the executing status.
    [Fact]
    public async Task APublishingJob_HoldsItsUnitUntilPublicationEnds()
    {
        var publishing = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        await this.ClaimWithExpiredLeaseAsync(publishing.Id, "host-a");
        Assert.True(await this._store.TryMarkPublishingAsync(publishing.Id));

        Assert.Empty(await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)));
        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);

        await this._repo.SetResultAsync(publishing.Id, new ReviewResult("summary", []));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        Assert.Equal(1, await this._dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing));
    }

    // A publication that outlives its timeout is failed rather than reclaimed, and that move out of
    // Processing returns the unit. The timeout is passed as a negative interval, which puts the sweep's
    // cutoff ahead of the publication stamp, so the publication counts as overdue without a wait.
    [Fact]
    public async Task APublicationFailedForItsTimeout_ReturnsItsUnit()
    {
        var stuck = await this.AddPendingJobAsync(prId: 1);
        var waiting = await this.AddPendingJobAsync(prId: 2);
        Assert.NotNull(await this._store.TryClaimAsync(stuck.Id, "host-a", LeaseDuration));
        Assert.True(await this._store.TryMarkPublishingAsync(stuck.Id));
        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);

        Assert.Equal([stuck.Id], await this._store.FailTimedOutPublicationsAsync(10, TimeSpan.FromSeconds(-1)));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
    }

    /// <summary>
    ///     Claims a job with an expiry that is already in the past, which is the row a host leaves behind
    ///     when it stops renewing. The claim is granted because the job is pending and the statement records
    ///     an expiry either way, and the negative duration puts the row in the state the sweep selects,
    ///     without waiting for real time to pass.
    /// </summary>
    private async Task<ReviewJobLease> ClaimWithExpiredLeaseAsync(Guid jobId, string owner)
    {
        var lease = await this._store.TryClaimAsync(jobId, owner, TimeSpan.FromSeconds(-1));
        Assert.NotNull(lease);
        return lease;
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

    /// <summary>
    ///     Writes one mention reply job with status Processing. The client row exists for that table's
    ///     foreign key; review jobs carry no such constraint, so AddPendingJobAsync needs none.
    /// </summary>
    private async Task<MentionReplyJob> AddProcessingMentionReplyJobAsync()
    {
        if (!await this._dbContext.Clients.AnyAsync(client => client.Id == MentionClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = MentionClientId,
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "Mention client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        var mention = new MentionReplyJob(
            Guid.NewGuid(),
            MentionClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            "10",
            100,
            "what does this do?")
        {
            Status = MentionJobStatus.Processing,
            ProcessingStartedAt = DateTimeOffset.UtcNow,
        };

        this._dbContext.MentionReplyJobs.Add(mention);
        await this._dbContext.SaveChangesAsync();
        return mention;
    }

    // Both rows are removed here because this class is the only one that writes them, and the mention job
    // must go first: its foreign key blocks deleting the client row while it exists.
    private async Task RemoveMentionReplyJobAsync(Guid mentionId)
    {
        await this._dbContext.MentionReplyJobs.Where(mention => mention.Id == mentionId).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(client => client.Id == MentionClientId).ExecuteDeleteAsync();
    }
}
