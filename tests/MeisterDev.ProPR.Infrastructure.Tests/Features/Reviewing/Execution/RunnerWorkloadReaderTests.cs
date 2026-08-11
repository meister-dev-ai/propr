// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     What the fleet is carrying is a join between reviews and the runner holding their lease, and the
///     lease records its owner as text rather than as a foreign key. Whether that join actually matches is
///     the kind of thing only a database can answer.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerWorkloadReaderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("a1a1a1a1-1111-4111-8111-a1a1a1a1a1a1");
    private static readonly Guid OtherTenantId = Guid.Parse("b2b2b2b2-2222-4222-8222-b2b2b2b2b2b2");
    private static readonly Guid ClientId = Guid.Parse("c3c3c3c3-3333-4333-8333-c3c3c3c3c3c3");

    private MeisterProPRDbContext _dbContext = null!;
    private RunnerWorkloadReader _reader = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);

        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.ReviewRunners.ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(client => client.Id == ClientId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(tenant => tenant.Id == TenantId).ExecuteDeleteAsync();

        await this._dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO tenants (id, display_name, slug, created_at, updated_at) VALUES ({0}, 'workload', 'workload-tests', now(), now())",
            TenantId);
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO clients (id, display_name, is_active, created_at, tenant_id) VALUES ({0}, 'workload', true, now(), {1})",
            ClientId,
            TenantId);

        this._reader = new RunnerWorkloadReader(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.ReviewRunners.ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(client => client.Id == ClientId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(tenant => tenant.Id == TenantId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task AReviewInFlight_IsAttributedToTheRunnerHoldingItsLease()
    {
        var runner = await this.EnrollAsync("runner-01");
        var job = await this.AddJobAsync(pullRequestId: 42);
        await this.LeaseAsync(job, runner, "Processing");

        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        var mine = Assert.Contains(runner.Id, workload.ByRunner);
        Assert.Equal(1, mine.ExecutingCount);
        Assert.Equal(42, Assert.Single(mine.Executing).PullRequestNumber);
        Assert.Equal(1, workload.ExecutingJobCount);
    }

    // The scoping that matters: one tenant's fleet must not report another tenant's work, or an operator
    // reads someone else's backlog as their own.
    [Fact]
    public async Task AnotherTenantsRunner_IsNotInThisTenantsFleet()
    {
        var mine = await this.EnrollAsync("runner-01");
        var theirs = await this.EnrollAsync("their-runner", OtherTenantId);
        await this.LeaseAsync(await this.AddJobAsync(1), mine, "Processing");
        await this.LeaseAsync(await this.AddJobAsync(2), theirs, "Processing");

        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.True(workload.ByRunner.ContainsKey(mine.Id));
        Assert.False(workload.ByRunner.ContainsKey(theirs.Id));
        Assert.Equal(1, workload.ExecutingJobCount);
    }

    // "Idle, and did twelve reviews today" and "idle, and has never done anything" are different states,
    // and only one of them is a problem.
    [Fact]
    public async Task ARunnerHoldingNothing_StillReportsWhatItFinished()
    {
        var runner = await this.EnrollAsync("runner-01");
        var job = await this.AddJobAsync(7);
        await this.LeaseAsync(job, runner, "Completed", completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        var mine = Assert.Contains(runner.Id, workload.ByRunner);
        Assert.Equal(0, mine.ExecutingCount);
        Assert.Equal(1, mine.CompletedCount);
        Assert.Empty(mine.Executing);
    }

    // The throughput figure covers a window rather than the runner's lifetime: a runner that did fifty reviews
    // last month and nothing since should not read as busy.
    [Fact]
    public async Task WorkFinishedBeforeTheWindow_IsNotCounted()
    {
        var runner = await this.EnrollAsync("runner-01");
        await this.LeaseAsync(
            await this.AddJobAsync(7),
            runner,
            "Completed",
            completedAt: DateTimeOffset.UtcNow.AddDays(-3));

        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(workload.ByRunner);
    }

    // Reported whether or not the queue counts as stalled. A queue that is merely deep and a queue that
    // nothing is taking look identical to an operator who can only see the second one.
    [Fact]
    public async Task WorkWaitingForTheFleet_IsReportedWithItsAge()
    {
        await this.EnrollAsync("runner-01");
        var waiting = await this.AddJobAsync(9);
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE review_jobs SET submitted_at = {0} WHERE id = {1}",
            DateTimeOffset.UtcNow.AddMinutes(-11),
            waiting.Id);

        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(1, workload.PendingJobCount);
        Assert.NotNull(workload.OldestPendingSince);
        Assert.True(workload.OldestPendingSince < DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    // A tenant with no runners has no fleet to report on, and asking the jobs about it would be a query
    // whose answer is always the same.
    [Fact]
    public async Task ATenantWithNoRunners_ReportsAnEmptyFleet()
    {
        var workload = await this._reader.GetWorkloadAsync(TenantId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(workload.ByRunner);
        Assert.Equal(0, workload.PendingJobCount);
        Assert.Null(workload.OldestPendingSince);
    }

    private async Task<ReviewRunner> EnrollAsync(string name, Guid? tenantId = null)
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(),
            tenantId ?? TenantId,
            name,
            [],
            2,
            "hashed:secret",
            $"LOOKUP{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);

        this._dbContext.ReviewRunners.Add(runner);
        await this._dbContext.SaveChangesAsync();
        return runner;
    }

    private async Task<ReviewJob> AddJobAsync(int pullRequestId)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            ClientId,
            "https://forge.invalid/org",
            "project",
            "repo",
            pullRequestId,
            1);

        this._dbContext.ReviewJobs.Add(job);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();
        return job;
    }

    /// <summary>
    ///     Written straight to the columns rather than through the lease store, because what is under test
    ///     is the read: the store's own transitions are covered where they live.
    /// </summary>
    private async Task LeaseAsync(
        ReviewJob job,
        ReviewRunner runner,
        string status,
        DateTimeOffset? completedAt = null)
    {
        await this._dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = {2},
                lease_owner = {1},
                lease_generation = 1,
                pr_repository_name = 'repo',
                processing_started_at = now()
            WHERE id = {0}
            """,
            job.Id,
            runner.Id.ToString("D"),
            status);

        if (completedAt is not null)
        {
            await this._dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE review_jobs SET completed_at = {1} WHERE id = {0}",
                job.Id,
                completedAt.Value);
        }
    }
}
