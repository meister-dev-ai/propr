// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Intake;

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
        ReviewJob? persisted = null;
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                persisted = call.ArgAt<ReviewJob>(0);
                return Task.FromResult(new TryAddReviewJobResult(true, null, 0));
            });
        var queued = new List<Guid>();
        queue.When(q => q.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(call => queued.Add(call.ArgAt<Guid>(0)));

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

        // The job that was stored, the id the caller was given and the id that was queued are one review.
        // Asserting them separately passes even when the three disagree.
        Assert.NotNull(persisted);
        Assert.Equal(persisted!.Id, result.NewJobId);
        Assert.Equal(persisted.Id, Assert.Single(queued));
    }

    [Theory]
    [InlineData(JobStatus.BudgetHeld)]
    [InlineData(JobStatus.BudgetExceeded)]
    public async Task HandleAsync_WhenBudgetBlocked_RetiresSourceThenClonesAndQueues(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);
        jobs.TrySupersedeAsync(job.Id, status, Arg.Any<CancellationToken>()).Returns(RetiredFrom(status));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.Restarted, result.Outcome);
        Assert.NotNull(result.NewJobId);

        // The budget-blocked source is retired first so the same-revision clone is not rejected as a duplicate.
        await jobs.Received(1).TrySupersedeAsync(job.Id, status, Arg.Any<CancellationToken>());
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
            Arg.Is<ReviewJob>(j =>
                j.Id != job.Id &&
                j.PullRequestId == job.PullRequestId &&
                j.IterationId == job.IterationId &&
                j.Status == JobStatus.Pending),
            Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(result.NewJobId!.Value, Arg.Any<CancellationToken>());
    }

    // A refused review restarts once the pull request is split or the bound is raised. The source is already
    // terminal and holds nothing live for its pull request, so it is cloned without being retired first.
    [Fact]
    public async Task HandleAsync_WhenAdmissionRefused_ClonesAndQueuesWithoutRetiringTheSource()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.AdmissionRefused;
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.Restarted, result.Outcome);
        Assert.NotNull(result.NewJobId);
        await jobs.DidNotReceive().TrySupersedeAsync(Arg.Any<Guid>(), Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
            Arg.Is<ReviewJob>(j =>
                j.Id != job.Id &&
                j.PullRequestId == job.PullRequestId &&
                j.IterationId == job.IterationId &&
                j.Status == JobStatus.Pending),
            Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(result.NewJobId!.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenFailed_DoesNotRetireTheSource()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.Failed;
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        await jobs.DidNotReceive().TrySupersedeAsync(Arg.Any<Guid>(), Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_WhenFailed_ClonesWhetherTheReviewWasAskedForExplicitly(bool allowUnchangedResubmission)
    {
        // Restarting a review someone asked for must not produce a job that execution deletes on sight for
        // having nothing new to review, which is exactly what a clone that dropped this would be.
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.Failed;
        job.SetAllowUnchangedResubmission(allowUnchangedResubmission);
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.Restarted, result.Outcome);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
            Arg.Is<ReviewJob>(clone => clone.AllowUnchangedResubmission == allowUnchangedResubmission),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(JobStatus.BudgetHeld)]
    [InlineData(JobStatus.BudgetExceeded)]
    public async Task HandleAsync_WhenBudgetBlockedAndAnotherJobIsLive_LeavesTheSourceRestartable(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);
        var live = MakeJob();
        live.Status = JobStatus.Pending;
        jobs.FindActiveJob(job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId)
            .Returns(live);

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);

        // Retiring the source before finding the duplicate would leave the operator with a job that is
        // neither running nor restartable.
        await jobs.DidNotReceive().TrySupersedeAsync(Arg.Any<Guid>(), Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(JobStatus.BudgetHeld)]
    [InlineData(JobStatus.BudgetExceeded)]
    public async Task HandleAsync_WhenBudgetBlockedAndTheCloneIsRejected_PutsTheSourceBack(JobStatus status)
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = status;
        jobs.GetById(job.Id).Returns(job);
        var retired = RetiredFrom(status);
        jobs.TrySupersedeAsync(job.Id, status, Arg.Any<CancellationToken>()).Returns(retired);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, MakeJob(), 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);
        await jobs.Received(1).TrySupersedeAsync(job.Id, status, Arg.Any<CancellationToken>());

        // The whole pre-supersede state goes back, not the status alone: a budget-blocked job that is still
        // restartable carries no completion time, and a lease it held is its own again.
        await jobs.Received(1).TryRestoreSupersededAsync(job.Id, retired, Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // Two restarts can read the same budget-blocked source. Only the one whose supersede changed the row may
    // put it back; the other would revive a source the winner has already cloned.
    [Fact]
    public async Task HandleAsync_WhenAnotherRequestRetiredTheSource_LeavesItRetired()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>())
            .Returns((SupersededReviewJobState?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, MakeJob(), 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);
        await jobs.DidNotReceive().TryRestoreSupersededAsync(
            Arg.Any<Guid>(),
            Arg.Any<SupersededReviewJobState>(),
            Arg.Any<CancellationToken>());
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The caller asked for a restart, so what failed the restart is what it has to be told. A failure of the
    // undo on top of that says nothing about why the restart did not happen.
    [Fact]
    public async Task HandleAsync_WhenPuttingTheSourceBackAlsoFails_ReportsTheFailureThatStoppedTheRestart()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>())
            .Returns(RetiredFrom(JobStatus.BudgetHeld));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns<TryAddReviewJobResult>(_ => throw new InvalidOperationException("the store is unreachable"));
        jobs.TryRestoreSupersededAsync(job.Id, Arg.Any<SupersededReviewJobState>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new TimeoutException("the store did not answer"));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(new RestartReviewJobCommand(job.Id)));

        Assert.Equal("the store is unreachable", thrown.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenBudgetBlockedAndTheInsertFails_PutsTheSourceBack()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        var retired = RetiredFrom(JobStatus.BudgetHeld);
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>()).Returns(retired);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns<TryAddReviewJobResult>(_ => throw new InvalidOperationException("the store is unreachable"));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(new RestartReviewJobCommand(job.Id)));

        await jobs.Received(1).TryRestoreSupersededAsync(job.Id, retired, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheSourceWasNotRetired_LeavesItsStatusAloneOnARejectedClone()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.Failed;
        jobs.GetById(job.Id).Returns(job);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, MakeJob(), 0));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);
        Assert.Equal(JobStatus.Failed, job.Status);
        await jobs.DidNotReceive().TrySupersedeAsync(Arg.Any<Guid>(), Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryRestoreSupersededAsync(
            Arg.Any<Guid>(),
            Arg.Any<SupersededReviewJobState>(),
            Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryTransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<JobStatus>(),
            Arg.Any<JobStatus>(),
            Arg.Any<CancellationToken>());
    }

    // A supersede that changed the row and then failed on the way back returns no state to put back. The state
    // read off the source before the call is what the restore uses, so the operator keeps a restartable job.
    [Fact]
    public async Task HandleAsync_WhenTheSupersedeFails_PutsTheSourceBackFromTheStateItHeldBefore()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>())
            .Returns<SupersededReviewJobState?>(_ => throw new TimeoutException("the store did not answer"));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.HandleAsync(new RestartReviewJobCommand(job.Id)));

        await jobs.Received(1).TryRestoreSupersededAsync(
            job.Id,
            Arg.Is<SupersededReviewJobState>(state => state.Status == JobStatus.BudgetHeld && state.CompletedAt == null),
            Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    // An insert can commit and still fail on the way back. The restart row is then the clone the source was
    // retired for, and putting the source back would leave two live jobs for one pull request.
    [Fact]
    public async Task HandleAsync_WhenTheInsertFailsAfterTheRestartLanded_LeavesTheSourceRetired()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        // Any id other than the source's is the restart the handler created, and the store has that row.
        jobs.GetById(Arg.Is<Guid>(id => id != job.Id)).Returns(MakeJob());
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>())
            .Returns(RetiredFrom(JobStatus.BudgetHeld));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns<TryAddReviewJobResult>(_ => throw new InvalidOperationException("the store is unreachable"));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(new RestartReviewJobCommand(job.Id)));

        await jobs.DidNotReceive().TryRestoreSupersededAsync(
            Arg.Any<Guid>(),
            Arg.Any<SupersededReviewJobState>(),
            Arg.Any<CancellationToken>());
    }

    // The rejected clone is reported to the operator as a duplicate. A restore that fails on top of that says
    // nothing about the request, so it does not become the outcome.
    [Fact]
    public async Task HandleAsync_WhenPuttingTheSourceBackFailsOnARejectedClone_StillReportsTheDuplicate()
    {
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IReviewExecutionQueue>();
        var job = MakeJob();
        job.Status = JobStatus.BudgetHeld;
        jobs.GetById(job.Id).Returns(job);
        jobs.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld, Arg.Any<CancellationToken>())
            .Returns(RetiredFrom(JobStatus.BudgetHeld));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, MakeJob(), 0));
        jobs.TryRestoreSupersededAsync(job.Id, Arg.Any<SupersededReviewJobState>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new TimeoutException("the store did not answer"));

        var sut = new RestartReviewJobHandler(jobs, queue, NullLogger<RestartReviewJobHandler>.Instance);

        var result = await sut.HandleAsync(new RestartReviewJobCommand(job.Id));

        Assert.Equal(RestartReviewJobOutcome.DuplicateActiveJob, result.Outcome);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The state the repository reports for a source it retired from a budget-blocked status.</summary>
    private static SupersededReviewJobState RetiredFrom(JobStatus status)
    {
        return new SupersededReviewJobState(status, CompletedAt: null, LeaseOwner: null, LeaseExpiresAt: null, LastHeartbeatAt: null);
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
