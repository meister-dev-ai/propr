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
///     The ceiling on how long one execution of a review may run. A job that keeps heartbeating is never
///     taken off its holder, and one has run for 21 hours that way, so the ceiling is applied by refusing
///     the renewal.
/// </summary>
public sealed class InMemoryReviewJobLeaseStoreDurationCeilingTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task TryRenewAsync_WithinTheCeiling_KeepsRenewing()
    {
        var (store, jobs, job) = await CreateClaimedJobAsync(maxReviewDurationMinutes: 60);
        var lease = await ClaimAsync(store, job);
        job.ProcessingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        var renewal = await store.TryRenewAsync(lease, LeaseDuration);

        Assert.True(renewal.Accepted);
        Assert.Equal(ReviewJobDirective.Continue, renewal.Directive);
        Assert.Equal(JobStatus.Processing, jobs.GetById(job.Id)!.Status);
    }

    [Fact]
    public async Task TryRenewAsync_PastTheCeiling_StopsAndFailsTheJob()
    {
        var (store, jobs, job) = await CreateClaimedJobAsync(maxReviewDurationMinutes: 60);
        var lease = await ClaimAsync(store, job);
        job.ProcessingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-61);

        var renewal = await store.TryRenewAsync(lease, LeaseDuration);

        Assert.False(renewal.Accepted);
        Assert.Equal(ReviewJobDirective.Stop, renewal.Directive);
        Assert.Equal(ReviewJobStopReason.MaxDurationExceeded, renewal.StopReason);

        // The job is failed with the reason recorded. An expired lease would be treated as an abandonment,
        // and the job would be reclaimed and run again to the same outcome.
        var current = jobs.GetById(job.Id)!;
        Assert.Equal(JobStatus.Failed, current.Status);
        Assert.Contains("60-minute", current.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(ReviewJobFailureReason.MaxDurationExceeded, current.FailureReason);
    }

    /// <summary>
    ///     The transition the ceiling makes, on its own. A review runs on one thread while the heartbeat
    ///     renews on another, so a review can reach its own end state between the check that finds the
    ///     ceiling passed and the write that acts on it. The durable store decides both in one conditional
    ///     statement; this is the offline equivalent, and what the lease store reports back depends on its
    ///     answer. The interleaving itself is not reproducible from a test, so the transition is asserted
    ///     directly.
    /// </summary>
    [Fact]
    public async Task TryFailWhileProcessing_ForAJobThatAlreadyCompleted_ChangesNothingAndSaysSo()
    {
        var jobs = new InMemoryReviewJobRepository();
        var processing = CreateJob();
        var completed = CreateJob();
        await jobs.AddAsync(processing);
        await jobs.AddAsync(completed);
        processing.Status = JobStatus.Processing;
        completed.Status = JobStatus.Processing;
        await jobs.SetResultAsync(completed.Id, new ReviewResult("A completed review.", new List<ReviewComment>().AsReadOnly()));

        Assert.True(jobs.TryFailWhileProcessing(processing.Id, "stopped", ReviewJobFailureReason.MaxDurationExceeded));
        Assert.Equal(JobStatus.Failed, processing.Status);
        Assert.Equal(ReviewJobFailureReason.MaxDurationExceeded, processing.FailureReason);
        Assert.Equal("stopped", processing.ErrorMessage);

        Assert.False(jobs.TryFailWhileProcessing(completed.Id, "stopped", ReviewJobFailureReason.MaxDurationExceeded));
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(ReviewJobFailureReason.Unspecified, completed.FailureReason);
        Assert.Null(completed.ErrorMessage);
    }

    private static async Task<(InMemoryReviewJobLeaseStore Store, InMemoryReviewJobRepository Jobs, ReviewJob Job)>
        CreateClaimedJobAsync(int maxReviewDurationMinutes)
    {
        var jobs = new InMemoryReviewJobRepository();
        var store = new InMemoryReviewJobLeaseStore(
            jobs,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions { MaxReviewDurationMinutes = maxReviewDurationMinutes }));

        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            "repository",
            42,
            1);
        await jobs.AddAsync(job);
        return (store, jobs, job);
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            "repository",
            42,
            1);
    }

    private static async Task<ReviewJobLease> ClaimAsync(InMemoryReviewJobLeaseStore store, ReviewJob job)
    {
        var lease = await store.TryClaimAsync(job.Id, "host-a", LeaseDuration);
        Assert.NotNull(lease);
        return lease!;
    }
}
