// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     What the admission transitions do to the jobs the calling context is tracking. They write through
///     conditional statements, so the tracked copies have to be brought up to date afterwards, and a caller
///     sharing the context must not lose its own unsaved work to that.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewAdmissionTrackedJobTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    // Every job this fixture makes carries this organization, so its cleanup reaches its own rows and leaves
    // the rows of tests running beside it on the shared database alone.
    private const string FixtureOrganizationUrl = "https://dev.azure.com/tracked-admission-fixture";

    private MeisterProPRDbContext _dbContext = null!;
    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private JobRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewJobs
            .Where(j => j.OrganizationUrl == FixtureOrganizationUrl)
            .ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext, new TestDbContextFactory(this._options), NullLogger<JobRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.ReviewJobs
                .Where(j => j.OrganizationUrl == FixtureOrganizationUrl)
                .ExecuteDeleteAsync();
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseDueAdmissionHoldsAsync_KeepsUnsavedChangesToOtherJobsInTheSameContext()
    {
        var held = MakeJob(prId: 71);
        await this._repo.AddAsync(held);
        await this._repo.SetAdmissionHeldAsync(held.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Another job the same context tracks, with an edit its caller has not saved yet.
        var other = MakeJob(prId: 72);
        await this._repo.AddAsync(other);
        other.RetryCount = 5;

        var released = await this._repo.ReleaseDueAdmissionHoldsAsync(DateTimeOffset.UtcNow);
        await this._dbContext.SaveChangesAsync();

        Assert.Equal(1, released);
        Assert.Equal(JobStatus.Pending, held.Status);

        await using var reader = new MeisterProPRDbContext(this._options);
        var storedOther = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == other.Id);
        Assert.Equal(5, storedOther.RetryCount);
    }

    [Fact]
    public async Task ReleaseDueAdmissionHoldsAsync_ShowsTheReleaseWithoutDiscardingUnsavedChangesToTheSameJob()
    {
        var held = MakeJob(prId: 73);
        await this._repo.AddAsync(held);
        await this._repo.SetAdmissionHeldAsync(held.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        // An edit on the released job itself, made before the release runs.
        held.RetryCount = 3;

        var released = await this._repo.ReleaseDueAdmissionHoldsAsync(DateTimeOffset.UtcNow);
        await this._dbContext.SaveChangesAsync();

        Assert.Equal(1, released);

        // The status the statement wrote is visible, and the edit the caller was holding is still there.
        Assert.Equal(JobStatus.Pending, held.Status);
        Assert.Equal(3, held.RetryCount);

        await using var reader = new MeisterProPRDbContext(this._options);
        var stored = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == held.Id);
        Assert.Equal(JobStatus.Pending, stored.Status);
        Assert.Equal(3, stored.RetryCount);
    }

    // A tracked job carrying an edit to a column the statement decides. Left in place, that edit is written
    // back by the next save and takes the transition with it.
    [Fact]
    public async Task TryTransitionAsync_TakesTheStatusItWroteOverAnEditThatWasHoldingTheOldOne()
    {
        var job = MakeJob(prId: 74);
        await this._repo.AddAsync(job);

        job.Status = JobStatus.Failed;
        job.RetryCount = 7;

        var moved = await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._dbContext.SaveChangesAsync();

        Assert.True(moved);
        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(7, job.RetryCount);

        await using var reader = new MeisterProPRDbContext(this._options);
        var stored = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Processing, stored.Status);
        Assert.Equal(7, stored.RetryCount);
    }

    [Fact]
    public async Task SetAdmissionHeldAsync_TakesTheHoldItWroteOverEditsToTheSameColumns()
    {
        var job = MakeJob(prId: 75);
        await this._repo.AddAsync(job);

        // A caller sharing the context edited the columns the hold decides, and one it does not.
        job.Status = JobStatus.Completed;
        job.SetAdmissionHold(DateTimeOffset.UtcNow.AddDays(1));
        job.RetryCount = 2;

        var heldUntil = DateTimeOffset.UtcNow.AddMinutes(30);
        await this._repo.SetAdmissionHeldAsync(job.Id, heldUntil);
        await this._dbContext.SaveChangesAsync();

        Assert.Equal(JobStatus.AdmissionHeld, job.Status);
        Assert.Equal(2, job.RetryCount);

        await using var reader = new MeisterProPRDbContext(this._options);
        var stored = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.AdmissionHeld, stored.Status);
        Assert.Equal(2, stored.RetryCount);

        // The hold the statement wrote is half an hour out; the edit it replaced was a day out.
        Assert.NotNull(stored.HeldUntil);
        Assert.True(stored.HeldUntil < heldUntil.AddHours(1), "the stored hold came from the edit the caller was holding");
        Assert.True(job.HeldUntil < heldUntil.AddHours(1), "the tracked job kept the hold the caller had edited in");
    }

    // A restart retires its source and puts it back when the clone is not added. Superseding stamps a
    // completion time, so a status put back on its own leaves a restartable job reporting that it completed.
    [Fact]
    public async Task TryRestoreSupersededAsync_PutsTheWholeStateBackForTheCallerThatRetiredTheJob()
    {
        var job = MakeJob(prId: 76);
        await this._repo.AddAsync(job);
        await this._repo.SetBudgetHeldAsync(job.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 10m, 12m);

        var retired = await this._repo.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld);

        Assert.NotNull(retired);
        Assert.Equal(JobStatus.BudgetHeld, retired!.Status);
        Assert.Equal(JobStatus.Superseded, job.Status);
        Assert.NotNull(job.CompletedAt);

        // A second caller reading the same job finds it retired already and is told so.
        Assert.Null(await this._repo.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld));

        Assert.True(await this._repo.TryRestoreSupersededAsync(job.Id, retired));

        await using var reader = new MeisterProPRDbContext(this._options);
        var stored = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.BudgetHeld, stored.Status);
        Assert.Null(stored.CompletedAt);
        Assert.Null(stored.LeaseOwner);
    }

    [Fact]
    public async Task TryRestoreSupersededAsync_LeavesAJobAnotherWriterHasMovedOnAlone()
    {
        var job = MakeJob(prId: 77);
        await this._repo.AddAsync(job);
        await this._repo.SetBudgetHeldAsync(job.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 10m, 12m);

        var retired = await this._repo.TrySupersedeAsync(job.Id, JobStatus.BudgetHeld);
        Assert.NotNull(retired);

        // Another writer decided a status of its own after the retirement.
        Assert.True(await this._repo.TryTransitionAsync(job.Id, JobStatus.Superseded, JobStatus.Failed));

        Assert.False(await this._repo.TryRestoreSupersededAsync(job.Id, retired!));

        await using var reader = new MeisterProPRDbContext(this._options);
        var stored = await reader.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
    }

    private static ReviewJob MakeJob(int prId)
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), FixtureOrganizationUrl, "proj", "repo", prId, 1);
    }
}
