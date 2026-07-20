// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Integration tests for the budget job-status transitions and the active-job queries that keep budget-blocked
///     jobs eligible for supersede and cancel, against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class JobBudgetStatusTests(PostgresContainerFixture fixture) : IAsyncLifetime
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
    public async Task SetBudgetExceededAsync_MarksARunningJobAndRecordsTheReason()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);

        await this._repo.SetBudgetExceededAsync(job.Id, BudgetScopeKind.Increment, BudgetCapKind.Hard, 5m, 6m);

        this._dbContext.ChangeTracker.Clear();
        var reloaded = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.BudgetExceeded, reloaded.Status);
        Assert.Equal(BudgetScopeKind.Increment, reloaded.BudgetBlockScope);
        Assert.Equal(BudgetCapKind.Hard, reloaded.BudgetBlockCapKind);
        Assert.Equal(5m, reloaded.BudgetBlockThresholdUsd);
        Assert.Equal(6m, reloaded.BudgetBlockSpentUsd);
    }

    [Fact]
    public async Task SetBudgetExceededAsync_DoesNotOverwriteADeliberateTerminalDecision()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetStoppedAsync(job.Id);

        await this._repo.SetBudgetExceededAsync(job.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, 100m, 100m);

        this._dbContext.ChangeTracker.Clear();
        var reloaded = await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Stopped, reloaded.Status);
        Assert.Null(reloaded.BudgetBlockScope);
    }

    [Fact]
    public async Task SetBudgetHeldAsync_HoldsAPendingJob_ButLeavesARunningJobUntouched()
    {
        var pending = MakeJob();
        await this._repo.AddAsync(pending);
        await this._repo.SetBudgetHeldAsync(pending.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 80m, 80m);

        var running = MakeJob();
        await this._repo.AddAsync(running);
        await this._repo.TryTransitionAsync(running.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetBudgetHeldAsync(running.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 80m, 80m);

        this._dbContext.ChangeTracker.Clear();
        Assert.Equal(JobStatus.BudgetHeld, (await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == pending.Id)).Status);
        Assert.Equal(JobStatus.Processing, (await this._dbContext.ReviewJobs.SingleAsync(j => j.Id == running.Id)).Status);
    }

    [Fact]
    public async Task GetActiveJobsForConfigAsync_IncludesBudgetHeldAndBudgetExceededJobs()
    {
        var held = MakeJob();
        await this._repo.AddAsync(held);
        await this._repo.SetBudgetHeldAsync(held.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 80m, 80m);

        var exceeded = MakeJob(prId: 2);
        await this._repo.AddAsync(exceeded);
        await this._repo.TryTransitionAsync(exceeded.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetBudgetExceededAsync(exceeded.Id, BudgetScopeKind.Increment, BudgetCapKind.Hard, 5m, 6m);

        var active = await this._repo.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "proj");

        // Both budget-blocked jobs are still "live" so a PR close can cancel them and a new push can supersede them.
        Assert.Contains(active, j => j.Id == held.Id);
        Assert.Contains(active, j => j.Id == exceeded.Id);
    }

    private static ReviewJob MakeJob(int prId = 1, int iterationId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", prId, iterationId);
    }
}
