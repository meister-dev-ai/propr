// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake;

public sealed class StopReviewJobHandlerTests
{
    private static readonly Guid ClientId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task HandleAsync_WhenJobMissing_ReturnsNotFound()
    {
        var jobs = Substitute.For<IJobRepository>();
        var registry = Substitute.For<IReviewJobCancellationRegistry>();
        jobs.GetById(Arg.Any<Guid>()).Returns((ReviewJob?)null);

        var sut = new StopReviewJobHandler(jobs, registry, NullLogger<StopReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new StopReviewJobCommand(Guid.NewGuid()));

        Assert.Equal(StopReviewJobOutcome.NotFound, result.Outcome);
        await jobs.DidNotReceive().SetStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        registry.DidNotReceive().Cancel(Arg.Any<Guid>());
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Superseded)]
    [InlineData(JobStatus.Stopped)]
    public async Task HandleAsync_WhenJobTerminal_ReturnsAlreadyFinishedWithoutStopping(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var registry = Substitute.For<IReviewJobCancellationRegistry>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);

        var sut = new StopReviewJobHandler(jobs, registry, NullLogger<StopReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new StopReviewJobCommand(job.Id));

        Assert.Equal(StopReviewJobOutcome.AlreadyFinished, result.Outcome);
        Assert.Equal(ClientId, result.ClientId);
        await jobs.DidNotReceive().SetStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        registry.DidNotReceive().Cancel(Arg.Any<Guid>());
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Processing)]
    public async Task HandleAsync_WhenJobActive_StopsAndSignalsCancellation(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var registry = Substitute.For<IReviewJobCancellationRegistry>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);

        var sut = new StopReviewJobHandler(jobs, registry, NullLogger<StopReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new StopReviewJobCommand(job.Id));

        Assert.Equal(StopReviewJobOutcome.Stopped, result.Outcome);
        Assert.Equal(ClientId, result.ClientId);
        await jobs.Received(1).SetStoppedAsync(job.Id, Arg.Any<CancellationToken>());
        registry.Received(1).Cancel(job.Id);
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
