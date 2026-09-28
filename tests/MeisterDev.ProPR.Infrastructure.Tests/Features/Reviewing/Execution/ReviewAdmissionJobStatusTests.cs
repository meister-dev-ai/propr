// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Integration tests for the review-admission job transitions against a real PostgreSQL instance: the hold
///     that resolves on its own, the refusal that carries its reason, and the burst count admission reads.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewAdmissionJobStatusTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task SetAdmissionRefusedAsync_EndsTheJobAndStoresTheReason()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);

        var refused = await this._repo.SetAdmissionRefusedAsync(
            job.Id, "Review not started: 312 changed files exceed the limit of 150.", policyFingerprint: null);

        Assert.True(refused);
        this._dbContext.ChangeTracker.Clear();
        var reloaded = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.AdmissionRefused, reloaded.Status);
        Assert.Contains("312 changed files", reloaded.AdmissionRefusalReason, StringComparison.Ordinal);
        Assert.NotNull(reloaded.CompletedAt);
    }

    [Fact]
    public async Task SetAdmissionRefusedAsync_RefusesOnce_SoARedispatchPostsNoSecondNotice()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        Assert.True(await this._repo.SetAdmissionRefusedAsync(job.Id, "too large", policyFingerprint: null));
        Assert.False(await this._repo.SetAdmissionRefusedAsync(job.Id, "too large", policyFingerprint: null));
    }

    // A refusal ends the job, and every terminal transition closes the protocols the job still has open. An
    // open record keeps a pass reading as running and leaves its tokens unmetered.
    [Fact]
    public async Task SetAdmissionRefusedAsync_ClosesTheProtocolsTheJobStillHasOpen()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        var open = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.ReviewJobProtocols.Add(open);
        await this._dbContext.SaveChangesAsync();

        Assert.True(await this._repo.SetAdmissionRefusedAsync(job.Id, "too large", policyFingerprint: null));

        this._dbContext.ChangeTracker.Clear();
        var stored = await this._dbContext.ReviewJobProtocols.AsNoTracking().SingleAsync(p => p.Id == open.Id);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal("Abandoned", stored.Outcome);
    }

    // The bound this window feeds is on the reviews an AI performed. A job still queued, a refused job and a
    // job waiting out its hold had no model call made for them, so counting them would spend the pull
    // request's hourly allowance on reviews that never ran.
    [Fact]
    public async Task GetSubmissionWindowAsync_LeavesOutJobsThatMadeNoModelCall()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId, prId: 13);
        await this._repo.AddAsync(asking);
        var refused = MakeJob(clientId, prId: 13);
        await this._repo.AddAsync(refused);
        Assert.True(await this._repo.SetAdmissionRefusedAsync(refused.Id, "too large", policyFingerprint: null));
        var held = MakeJob(clientId, prId: 13);
        await this._repo.AddAsync(held);
        await this._repo.SetAdmissionHeldAsync(held.Id, DateTimeOffset.UtcNow.AddMinutes(30));
        // Still in the queue, so nothing was spent on it yet.
        await this._repo.AddAsync(MakeJob(clientId, prId: 13));
        // A review that ended before its first model call.
        var endedEarly = MakeJob(clientId, prId: 13);
        endedEarly.Status = JobStatus.Failed;
        await this._repo.AddAsync(endedEarly);
        // A review that ran, and one that is running and about to call the model.
        await this._repo.AddAsync(MakeJob(clientId, prId: 13, inputTokens: 1_200, outputTokens: 400));
        var running = MakeJob(clientId, prId: 13);
        running.Status = JobStatus.Processing;
        await this._repo.AddAsync(running);

        var window = await this._repo.GetSubmissionWindowAsync(
            clientId, "https://dev.azure.com/org", "proj", "repo", 13, DateTimeOffset.UtcNow.AddHours(-1), asking.Id);

        Assert.Equal(2, window.Count);
    }

    // A push that arrives while the previous review is running supersedes it. The superseded review has
    // already spent what the bound measures, so it counts; a review superseded before its first model call
    // spent nothing and does not.
    [Fact]
    public async Task GetSubmissionWindowAsync_CountsASupersededJobByWhatItSpent()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId, prId: 14);
        await this._repo.AddAsync(asking);
        var spent = MakeJob(clientId, prId: 14, inputTokens: 900, outputTokens: 300);
        await this._repo.AddAsync(spent);
        await this._repo.SetSupersededAsync(spent.Id);
        var spentNothing = MakeJob(clientId, prId: 14);
        await this._repo.AddAsync(spentNothing);
        await this._repo.SetSupersededAsync(spentNothing.Id);

        var window = await this._repo.GetSubmissionWindowAsync(
            clientId, "https://dev.azure.com/org", "proj", "repo", 14, DateTimeOffset.UtcNow.AddHours(-1), asking.Id);

        Assert.Equal(1, window.Count);
    }

    [Fact]
    public async Task SetAdmissionHeldAsync_HoldsAQueuedJobAndReleasesItOnceTheWindowHasPassed()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        var admissibleAt = DateTimeOffset.UtcNow.AddHours(1);

        await this._repo.SetAdmissionHeldAsync(job.Id, admissibleAt);

        this._dbContext.ChangeTracker.Clear();
        var held = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.AdmissionHeld, held.Status);
        Assert.NotNull(held.HeldUntil);

        // Still inside the window: nothing is returned to the queue.
        Assert.Equal(0, await this._repo.ReleaseDueAdmissionHoldsAsync(admissibleAt.AddMinutes(-1)));

        Assert.Equal(1, await this._repo.ReleaseDueAdmissionHoldsAsync(admissibleAt));
        this._dbContext.ChangeTracker.Clear();
        var released = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Pending, released.Status);
        Assert.Null(released.HeldUntil);
    }

    [Fact]
    public async Task GetSubmissionWindowAsync_CountsTheSamePullRequestAndLeavesTheAskingJobOut()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId, prId: 7);
        await this._repo.AddAsync(asking);
        await this._repo.AddAsync(MakeJob(clientId, prId: 7, inputTokens: 1_000, outputTokens: 200));
        await this._repo.AddAsync(MakeJob(clientId, prId: 7, inputTokens: 1_000, outputTokens: 200));
        // Another pull request of the same client does not count.
        await this._repo.AddAsync(MakeJob(clientId, prId: 8, inputTokens: 1_000, outputTokens: 200));

        var window = await this._repo.GetSubmissionWindowAsync(
            clientId, "https://dev.azure.com/org", "proj", "repo", 7, DateTimeOffset.UtcNow.AddHours(-1), asking.Id);

        Assert.Equal(2, window.Count);
    }

    [Fact]
    public async Task GetSubmissionWindowAsync_LeavesOutWhatWasSubmittedBeforeTheWindow()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId, prId: 9);
        await this._repo.AddAsync(asking);
        // One submission on each side of the window, so the count proves what the boundary does rather than
        // that a cutoff in the future excludes everything.
        await this._repo.AddAsync(MakeJobSubmittedAt(clientId, prId: 9, submittedAt: DateTimeOffset.UtcNow.AddMinutes(-90)));
        await this._repo.AddAsync(MakeJobSubmittedAt(clientId, prId: 9, submittedAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        var window = await this._repo.GetSubmissionWindowAsync(
            clientId, "https://dev.azure.com/org", "proj", "repo", 9, DateTimeOffset.UtcNow.AddHours(-1), asking.Id);

        Assert.Equal(1, window.Count);
    }

    [Fact]
    public async Task SetAdmissionHeldAsync_LeavesAJobAnotherWorkerClaimed_InProcessing()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        // The claim another worker won between the candidate read and this call.
        Assert.True(await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing));

        await this._repo.SetAdmissionHeldAsync(job.Id, DateTimeOffset.UtcNow.AddHours(1));

        this._dbContext.ChangeTracker.Clear();
        var stored = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Null(stored.HeldUntil);
    }

    [Fact]
    public async Task ReleaseDueAdmissionHoldsAsync_LeavesAHoldThatWasSupersededMeanwhile_Terminal()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        var admissibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await this._repo.SetAdmissionHeldAsync(job.Id, admissibleAt);
        // A newer push supersedes the held job before the tick that would release it.
        await this._repo.SetSupersededAsync(job.Id);

        var released = await this._repo.ReleaseDueAdmissionHoldsAsync(DateTimeOffset.UtcNow);

        Assert.Equal(0, released);
        this._dbContext.ChangeTracker.Clear();
        var stored = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Superseded, stored.Status);
    }

    [Fact]
    public async Task SetAdmissionRefusedAsync_RefusesAgainstThePersistedStatus_NotATrackedCopy()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        Assert.True(await this._repo.SetAdmissionRefusedAsync(job.Id, "too large", policyFingerprint: null));

        // A fresh context and repository, so the second call decides on the persisted row alone.
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        await using var secondContext = new MeisterProPRDbContext(options);
        var secondRepo = new JobRepository(secondContext, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);

        Assert.False(await secondRepo.SetAdmissionRefusedAsync(job.Id, "too large", policyFingerprint: null));
    }

    [Fact]
    public async Task GetSubmissionWindowAsync_ReportsTheOldestSubmissionSoTheHoldEndsWithIt()
    {
        var clientId = Guid.NewGuid();
        var asking = MakeJob(clientId, prId: 11);
        await this._repo.AddAsync(asking);

        // Every timestamp and the query cutoff come from one window start, so the time the inserts take cannot
        // move a submission across the boundary.
        var windowStart = TruncateToStoredPrecision(DateTimeOffset.UtcNow.AddHours(-1));
        var oldest = windowStart.AddMinutes(1);
        await this._repo.AddAsync(MakeJobSubmittedAt(clientId, prId: 11, submittedAt: oldest));
        await this._repo.AddAsync(MakeJobSubmittedAt(clientId, prId: 11, submittedAt: windowStart.AddMinutes(55)));
        // Submitted before the window starts, so it is neither counted nor reported as the oldest.
        await this._repo.AddAsync(MakeJobSubmittedAt(clientId, prId: 11, submittedAt: windowStart.AddHours(-2)));

        var window = await this._repo.GetSubmissionWindowAsync(clientId, "https://dev.azure.com/org", "proj", "repo", 11, windowStart, asking.Id);

        Assert.Equal(2, window.Count);
        Assert.NotNull(window.OldestSubmittedAt);
        Assert.Equal(oldest, window.OldestSubmittedAt!.Value);
    }

    /// <summary>
    ///     Rounds a timestamp down to the microsecond the timestamptz column keeps, so a value written and read
    ///     back compares equal without the comparison having to drop precision of its own.
    /// </summary>
    private static DateTimeOffset TruncateToStoredPrecision(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);
    }

    // The jobs the window has to count are the ones a model call was made for, so a helper job carries the
    // tokens of one call unless a test wants a job that spent nothing.
    /// <summary>
    /// The refusal records the bounds it was decided under, and the head it refused is readable from them.
    ///     An automatic trigger uses both to avoid a second job for the same head.
    /// </summary>
    [Fact]
    public async Task GetLatestRefusedAdmissionAsync_ReturnsTheRefusedHeadAndTheLimitsItWasRefusedUnder()
    {
        var clientId = Guid.NewGuid();
        var job = MakeJob(clientId, prId: 77);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "revision-a", null));
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);

        Assert.True(await this._repo.SetAdmissionRefusedAsync(job.Id, "too large", "///5"));

        this._dbContext.ChangeTracker.Clear();
        var refused = await this._repo.GetLatestRefusedAdmissionAsync(
            clientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            77);

        Assert.NotNull(refused);
        Assert.Equal("revision-a", refused.StoredRevisionKey);
        Assert.Equal("///5", refused.PolicyFingerprint);
    }

    // A pull request nothing refused has no refusal to read, so an automatic trigger reviews it.
    [Fact]
    public async Task GetLatestRefusedAdmissionAsync_WithoutARefusal_ReturnsNull()
    {
        var clientId = Guid.NewGuid();
        var job = MakeJob(clientId, prId: 78);
        await this._repo.AddAsync(job);

        var refused = await this._repo.GetLatestRefusedAdmissionAsync(
            clientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            78);

        Assert.Null(refused);
    }

    private static ReviewJob MakeJobSubmittedAt(Guid clientId, int prId, DateTimeOffset submittedAt)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", prId, 1)
        {
            SubmittedAt = submittedAt,
        };
        job.AccumulateTokens(1_000, 200);
        return job;
    }

    private static ReviewJob MakeJob(
        Guid? clientId = null,
        int prId = 1,
        int iterationId = 1,
        long inputTokens = 0,
        long outputTokens = 0)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId ?? Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", prId, iterationId);
        if (inputTokens > 0 || outputTokens > 0)
        {
            job.AccumulateTokens(inputTokens, outputTokens);
        }

        return job;
    }
}
