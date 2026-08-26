// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Offline;

/// <summary>
///     The capped claim on the in-memory store, which the offline evaluation path uses. A caller cannot tell
///     the two stores apart, so the ceiling holds here and the refusal carries the same numbers.
/// </summary>
public sealed class InMemoryReviewJobLeaseStoreTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly InMemoryReviewJobRepository _jobs = new();
    private readonly InMemoryReviewJobLeaseStore _store;

    public InMemoryReviewJobLeaseStoreTests()
    {
        this._store = new InMemoryReviewJobLeaseStore(
            this._jobs,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TryClaimWithinProcessingCap_FromManyClaimantsOnDistinctJobs_AdmitsExactlyTheCap(int cap)
    {
        const int claimants = 6;
        var jobs = new List<ReviewJob>(claimants);
        for (var i = 1; i <= claimants; i++)
        {
            jobs.Add(await this.AddPendingJobAsync(i));
        }

        // Every claimant waits on one gate and is released together, so the claims overlap. Awaiting them in
        // sequence would run each to completion before the next began, which a store that counted outside its
        // lock would also satisfy.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = jobs
            .Select((job, index) => Task.Run(async () =>
            {
                await start.Task;
                return await this._store.TryClaimWithinProcessingCapAsync(job.Id, $"host-{index}", LeaseDuration, cap);
            }))
            .ToList();

        start.SetResult();
        var results = await Task.WhenAll(claims);

        Assert.Equal(cap, results.Count(claim => claim.Outcome == ReviewJobCappedClaimOutcome.Granted));
        Assert.Equal(cap, await this._jobs.CountProcessingJobsAsync());
        Assert.All(
            results.Where(claim => claim.Outcome == ReviewJobCappedClaimOutcome.AtCapacity),
            claim =>
            {
                Assert.Equal(cap, claim.Cap);
                Assert.Equal(cap, claim.ProcessingCount);
            });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TryClaimWithinProcessingCap_AtTheCap_NamesTheCapAndTheObservedCount(int cap)
    {
        for (var prId = 1; prId <= cap; prId++)
        {
            var running = await this.AddPendingJobAsync(prId);
            Assert.NotNull(await this._store.TryClaimAsync(running.Id, $"host-{prId}", LeaseDuration));
        }

        var queued = await this.AddPendingJobAsync(cap + 1);

        var claim = await this._store.TryClaimWithinProcessingCapAsync(queued.Id, "host-b", LeaseDuration, cap);

        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, claim.Outcome);
        Assert.Null(claim.Lease);
        Assert.Equal(cap, claim.Cap);
        Assert.Equal(cap, claim.ProcessingCount);
        Assert.Equal(JobStatus.Pending, this._jobs.GetById(queued.Id)!.Status);
    }

    // Below the cap the refusal is about this job alone, so it carries no numbers: the rest of the queue is
    // still worth scanning.
    [Fact]
    public async Task TryClaimWithinProcessingCap_BelowTheCapOnAJobAlreadyTaken_SaysTheJobIsNotClaimable()
    {
        var job = await this.AddPendingJobAsync(1);
        Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-b", LeaseDuration, 3);

        Assert.Equal(ReviewJobCappedClaimOutcome.NotClaimable, claim.Outcome);
        Assert.Null(claim.Cap);
        Assert.Null(claim.ProcessingCount);
        Assert.Equal("host-a", this._jobs.GetById(job.Id)!.LeaseOwner);
    }

    // A job claimed under the cap has to be indistinguishable afterwards from one claimed without it.
    [Fact]
    public async Task TryClaimWithinProcessingCap_StampsWhatTheUncappedClaimStamps()
    {
        var job = await this.AddPendingJobAsync(1);

        var claim = await this._store.TryClaimWithinProcessingCapAsync(job.Id, "host-a", LeaseDuration, 1);

        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        Assert.NotNull(claim.Lease);
        Assert.Equal("host-a", claim.Lease.Owner);
        Assert.Equal(1, claim.Lease.Generation);

        var stored = this._jobs.GetById(job.Id)!;
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal("host-a", stored.LeaseOwner);
        Assert.Equal(1, stored.LeaseGeneration);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.NotNull(stored.LastHeartbeatAt);
        Assert.NotNull(stored.ProcessingStartedAt);
        Assert.Null(stored.PublishingStartedAt);
    }

    // The count is over the executing status here too, so a job that reaches a terminal state stops holding
    // its unit.
    [Theory]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task AJobThatReachesATerminalState_HoldsNoCapacity(string terminal)
    {
        var running = await this.AddPendingJobAsync(1);
        var waiting = await this.AddPendingJobAsync(2);
        Assert.Equal(
            ReviewJobCappedClaimOutcome.Granted,
            (await this._store.TryClaimWithinProcessingCapAsync(running.Id, "host-a", LeaseDuration, 1)).Outcome);
        Assert.Equal(
            ReviewJobCappedClaimOutcome.AtCapacity,
            (await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1)).Outcome);

        if (string.Equals(terminal, "failed", StringComparison.Ordinal))
        {
            await this._jobs.SetFailedAsync(running.Id, "boom");
        }
        else
        {
            await this._jobs.SetResultAsync(running.Id, new ReviewResult("summary", []));
        }

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
        Assert.Equal(1, await this._jobs.CountProcessingJobsAsync());
    }

    // A deliberate hand-back returns the unit. Repeating it carries a lease the job no longer holds, so
    // nothing further is returned. The count is derived from the status of each job, so a unit returned
    // twice is not representable; the repeat is checked for a refusal and an unchanged count.
    [Fact]
    public async Task ARepeatedRelease_IsRefusedAndReturnsNothingFurther()
    {
        var running = await this.AddPendingJobAsync(1);
        var first = await this.AddPendingJobAsync(2);
        var second = await this.AddPendingJobAsync(3);
        var lease = await this._store.TryClaimAsync(running.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        Assert.True(await this._store.TryReleaseAsync(lease));
        Assert.False(await this._store.TryReleaseAsync(lease));

        Assert.Equal(
            ReviewJobCappedClaimOutcome.Granted,
            (await this._store.TryClaimWithinProcessingCapAsync(first.Id, "host-b", LeaseDuration, 1)).Outcome);
        var refused = await this._store.TryClaimWithinProcessingCapAsync(second.Id, "host-c", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
    }

    // A failed hand-back returns the unit as well, and repeating it returns nothing further. The count is
    // derived from the status of each job, so a unit returned twice is not representable; the repeat is
    // checked for a refusal and an unchanged count.
    [Fact]
    public async Task ARepeatedFailedHandBack_IsRefusedAndReturnsNothingFurther()
    {
        var running = await this.AddPendingJobAsync(1);
        var first = await this.AddPendingJobAsync(2);
        var second = await this.AddPendingJobAsync(3);
        var lease = await this._store.TryClaimAsync(running.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);

        Assert.Equal(
            ReviewJobReclaimOutcome.Requeued,
            await this._store.TryReleaseFailedAsync(lease, maxConsecutiveReclaims: 3, maxTotalReclaims: 12));
        Assert.Equal(
            ReviewJobReclaimOutcome.NotReclaimed,
            await this._store.TryReleaseFailedAsync(lease, maxConsecutiveReclaims: 3, maxTotalReclaims: 12));

        Assert.Equal(
            ReviewJobCappedClaimOutcome.Granted,
            (await this._store.TryClaimWithinProcessingCapAsync(first.Id, "host-b", LeaseDuration, 1)).Outcome);
        var refused = await this._store.TryClaimWithinProcessingCapAsync(second.Id, "host-c", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
    }

    // A party that lost the job to another claimant carries an older generation, so its release returns
    // nothing and the unit stays with the current holder.
    [Fact]
    public async Task AStaleHoldersRelease_ReturnsNoFurtherCapacity()
    {
        var job = await this.AddPendingJobAsync(1);
        var waiting = await this.AddPendingJobAsync(2);
        var stale = await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(stale);
        Assert.True(await this._store.TryReleaseAsync(stale));
        var current = await this._store.TryClaimAsync(job.Id, "host-b", LeaseDuration);
        Assert.NotNull(current);
        Assert.Equal(stale.Generation + 1, current.Generation);

        Assert.False(await this._store.TryReleaseAsync(stale));
        Assert.Equal(
            ReviewJobReclaimOutcome.NotReclaimed,
            await this._store.TryReleaseFailedAsync(stale, maxConsecutiveReclaims: 3, maxTotalReclaims: 12));

        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-c", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
        Assert.Equal("host-b", this._jobs.GetById(job.Id)!.LeaseOwner);
    }

    // A publishing job holds its unit until the job leaves the executing status, as on the durable store.
    [Fact]
    public async Task APublishingJob_HoldsItsUnitUntilPublicationEnds()
    {
        var publishing = await this.AddPendingJobAsync(1);
        var waiting = await this.AddPendingJobAsync(2);
        Assert.NotNull(await this._store.TryClaimAsync(publishing.Id, "host-a", LeaseDuration));
        Assert.True(await this._store.TryMarkPublishingAsync(publishing.Id));

        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);

        await this._jobs.SetResultAsync(publishing.Id, new ReviewResult("summary", []));

        var claim = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.Granted, claim.Outcome);
    }

    // Expiry has no mirror here: the offline path runs one review at a time in one process, so this store
    // offers nothing for reclaim and an expired lease keeps its unit. Recovery from a lost holder is a
    // property of the durable store.
    [Fact]
    public async Task AnExpiredLease_IsNotOfferedForReclaimAndKeepsItsUnit()
    {
        var running = await this.AddPendingJobAsync(1);
        var waiting = await this.AddPendingJobAsync(2);
        // The negative duration stamps an expiry that is already past, so the premise holds without a wait.
        var lease = await this._store.TryClaimAsync(running.Id, "host-a", TimeSpan.FromSeconds(-1));
        Assert.NotNull(lease);

        Assert.Empty(await this._store.GetExpiredLeasesAsync(10, TimeSpan.Zero, TimeSpan.FromMinutes(30)));
        var refused = await this._store.TryClaimWithinProcessingCapAsync(waiting.Id, "host-b", LeaseDuration, 1);
        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, refused.Outcome);
        Assert.Equal(1, refused.ProcessingCount);
    }

    private async Task<ReviewJob> AddPendingJobAsync(int prId)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            prId,
            1);
        await this._jobs.AddAsync(job);
        return job;
    }
}
