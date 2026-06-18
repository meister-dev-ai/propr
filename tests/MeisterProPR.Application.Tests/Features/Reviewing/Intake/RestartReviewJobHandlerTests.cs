// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake;

public sealed class RestartReviewJobHandlerTests
{
    private static readonly Guid ClientId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task HandleAsync_WhenJobMissing_ReturnsNotFound()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        jobs.GetById(Arg.Any<Guid>()).Returns((ReviewJob?)null);

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(Guid.NewGuid()));

        Assert.Equal(RestartReviewJobOutcome.NotFound, result.Outcome);
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Processing)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task HandleAsync_WhenJobNotFailed_ReturnsNotFailed(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.NotFailed, result.Outcome);
        Assert.Equal(ClientId, result.ClientId);
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenFailed_ClonesCoordinatesQueuesNewJob()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.Failed;
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.Restarted, result.Outcome);
        Assert.NotNull(result.NewJobId);
        Assert.NotEqual(job.Id, result.NewJobId);
        Assert.Equal(ClientId, result.ClientId);

        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
            Arg.Is<ReviewJob>(j =>
                j.Id != job.Id &&
                j.ClientId == ClientId &&
                j.RepositoryId == job.RepositoryId &&
                j.PullRequestId == job.PullRequestId &&
                j.IterationId == job.IterationId &&
                j.Status == JobStatus.Pending),
            Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(result.NewJobId!.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenActiveDuplicateExists_ReturnsDuplicateAndDoesNotEnqueue()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.Failed;
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, job, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);
        Assert.Null(result.NewJobId);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static ReviewJob MakeJob()
    {
        return new ReviewJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/org",
            "project",
            "repo-1",
            42,
            7);
    }
}
