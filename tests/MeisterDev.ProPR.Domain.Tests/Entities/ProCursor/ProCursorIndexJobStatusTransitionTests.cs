// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Tests.Entities.ProCursor;

public sealed class ProCursorIndexJobStatusTransitionTests
{
    private static readonly DateTimeOffset Lease = DateTimeOffset.UtcNow.AddMinutes(5);

    private static ProCursorIndexJob CreateJob()
    {
        return new ProCursorIndexJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "refresh",
            "source/branch/head");
    }

    [Fact]
    public void Constructor_DefaultsStatusToPending()
    {
        var job = CreateJob();

        Assert.Equal(ProCursorIndexJobStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public void MarkProcessing_FromPending_SetsProcessingState()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var job = CreateJob();

        job.MarkProcessing(Lease);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Equal(ProCursorIndexJobStatus.Processing, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.InRange(job.StartedAt!.Value, before, after);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.FailureReason);
        Assert.Equal(Lease, job.LeaseExpiresAt);
    }

    [Fact]
    public void RenewLease_FromProcessing_ExtendsTheLease()
    {
        var job = CreateJob();
        job.MarkProcessing(Lease);

        job.RenewLease(Lease.AddMinutes(1));

        Assert.Equal(Lease.AddMinutes(1), job.LeaseExpiresAt);
    }

    [Fact]
    public void RenewLease_FromCancelled_Throws()
    {
        var job = CreateJob();
        job.MarkProcessing(Lease);
        job.MarkCancelled();

        Assert.Throws<InvalidOperationException>(() => job.RenewLease(Lease.AddMinutes(1)));

        // The service that ran the cancelled job can write until the lease expires.
        Assert.Equal(Lease, job.LeaseExpiresAt);
    }

    [Fact]
    public void MarkPendingForRetry_FromProcessing_ReleasesTheLease()
    {
        var job = CreateJob();
        job.MarkProcessing(Lease);

        job.MarkPendingForRetry("stopped");

        Assert.Equal(ProCursorIndexJobStatus.Pending, job.Status);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public void MarkCompleted_FromProcessing_SetsCompletedState()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var job = CreateJob();
        job.MarkProcessing(Lease);

        job.MarkCompleted();

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status);
        Assert.InRange(job.CompletedAt!.Value, before, after);
        Assert.Null(job.FailureReason);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public void MarkFailed_FromProcessing_SetsFailedState()
    {
        var job = CreateJob();
        job.MarkProcessing(Lease);

        job.MarkFailed("boom");

        Assert.Equal(ProCursorIndexJobStatus.Failed, job.Status);
        Assert.Equal("boom", job.FailureReason);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public void MarkCompleted_FromPending_Throws()
    {
        var job = CreateJob();

        Assert.Throws<InvalidOperationException>(job.MarkCompleted);
    }
}
