// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerCallAuthorizerTests
{
    private readonly IReviewJobExecutionStore _jobs = Substitute.For<IReviewJobExecutionStore>();

    private static ReviewJob LeasedJob(string owner = "runner-a", int generation = 4)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 7, 1);
        job.Status = JobStatus.Processing;
        job.ApplyLease(owner, generation, DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow);
        return job;
    }

    private RunnerCallAuthorizer CreateAuthorizer(ReviewJob? job)
    {
        this._jobs.GetById(Arg.Any<Guid>()).Returns(job);
        return new RunnerCallAuthorizer(this._jobs);
    }

    [Fact]
    public async Task TheCurrentLeaseHolder_IsServed()
    {
        var job = LeasedJob();

        var authorization = await this.CreateAuthorizer(job)
            .AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-a"));

        Assert.True(authorization.IsAuthorized);
        Assert.Equal(job.ClientId, authorization.ClientId);
    }

    // The case the whole authorization exists for: a runner whose lease was reclaimed, still working, still
    // calling. Whoever holds the job now owns its outcome, and serving this would let two parties write the
    // same review.
    [Fact]
    public async Task ACallerHoldingASupersededGeneration_IsRefused()
    {
        var job = LeasedJob(generation: 5);

        var authorization = await this.CreateAuthorizer(job)
            .AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-a"));

        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, authorization.Refusal);
    }

    [Fact]
    public async Task ACallerThatIsNotTheLeaseHolder_IsRefused()
    {
        var job = LeasedJob(owner: "runner-a");

        var authorization = await this.CreateAuthorizer(job)
            .AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-b"));

        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RunnerCallRefusal.NotTheLeaseHolder, authorization.Refusal);
    }

    // Two different problems. An operator reading the audit needs to see whether a runner was overtaken or
    // whether a caller is asking about a job it never held.
    [Fact]
    public async Task AnImpostorAndAnOvertakenRunner_AreRefusedForDifferentReasons()
    {
        var job = LeasedJob(owner: "runner-a", generation: 5);
        var authorizer = this.CreateAuthorizer(job);

        var overtaken = await authorizer.AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-a"));
        var impostor = await authorizer.AuthorizeAsync(new RunnerCallContext(job.Id, 5, "runner-b"));

        Assert.NotEqual(overtaken.Refusal, impostor.Refusal);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Stopped)]
    [InlineData(JobStatus.Superseded)]
    [InlineData(JobStatus.Pending)]
    public async Task AJobThatIsNotExecuting_HasNothingAnExecutorMayDoToIt(JobStatus status)
    {
        var job = LeasedJob();
        job.Status = status;

        var authorization = await this.CreateAuthorizer(job)
            .AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-a"));

        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RunnerCallRefusal.JobNotExecuting, authorization.Refusal);
    }

    [Fact]
    public async Task AnUnknownJob_IsRefusedWithoutSayingAnythingElse()
    {
        var authorization = await this.CreateAuthorizer(null)
            .AuthorizeAsync(new RunnerCallContext(Guid.NewGuid(), 1, "runner-a"));

        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RunnerCallRefusal.JobNotExecuting, authorization.Refusal);
    }

    // A refusal must not leak whose job it was; the caller has already failed to prove it may know.
    [Fact]
    public async Task ARefusal_CarriesNoClientIdentity()
    {
        var job = LeasedJob(owner: "runner-a");

        var authorization = await this.CreateAuthorizer(job)
            .AuthorizeAsync(new RunnerCallContext(job.Id, 4, "runner-b"));

        Assert.Equal(Guid.Empty, authorization.ClientId);
    }
}
