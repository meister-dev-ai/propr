// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Entities.ProCursor;

public sealed class ProCursorIndexJobStatusTransitionTests
{
    private static ProCursorIndexJob CreateJob() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "refresh", "source/branch/head");

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

        job.MarkProcessing();

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Equal(ProCursorIndexJobStatus.Processing, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.InRange(job.StartedAt!.Value, before, after);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.FailureReason);
    }

    [Fact]
    public void MarkCompleted_FromProcessing_SetsCompletedState()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var job = CreateJob();
        job.MarkProcessing();

        job.MarkCompleted();

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.Equal(ProCursorIndexJobStatus.Completed, job.Status);
        Assert.InRange(job.CompletedAt!.Value, before, after);
        Assert.Null(job.FailureReason);
    }

    [Fact]
    public void MarkFailed_FromProcessing_SetsFailedState()
    {
        var job = CreateJob();
        job.MarkProcessing();

        job.MarkFailed("boom");

        Assert.Equal(ProCursorIndexJobStatus.Failed, job.Status);
        Assert.Equal("boom", job.FailureReason);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void MarkCompleted_FromPending_Throws()
    {
        var job = CreateJob();

        Assert.Throws<InvalidOperationException>(job.MarkCompleted);
    }
}
