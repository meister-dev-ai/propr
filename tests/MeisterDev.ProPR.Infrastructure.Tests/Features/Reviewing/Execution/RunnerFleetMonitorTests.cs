// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Where reviews run, and whether a still queue is a stall. The predicate has four clauses and every
///     one of them is a way an installation can be wrongly told it has capacity.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerFleetMonitorTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherTenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

    // The real store, not a substitute. The counts and the client-eligibility set are decided in SQL, and
    // that is exactly the part a double would stop testing.
    private RunnerLeaseOfferStore _offers = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.ReviewRunners.ExecuteDeleteAsync();
        // Scoped to this class's own tenants. A blanket delete of clients and tenants also removes the
        // rows other suites in this collection seeded, which shows up as unrelated failures in a full run
        // and passes in isolation.
        await this._dbContext.Clients.Where(c => c.TenantId == TenantId || c.TenantId == OtherTenantId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => t.Id == TenantId || t.Id == OtherTenantId).ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext, new TestDbContextFactory(this._options), NullLogger<JobRepository>.Instance);
        this._offers = new RunnerLeaseOfferStore(this._dbContext);
        RunnerFleetMonitor.ResetHysteresis();
    }

    public async Task DisposeAsync()
    {
        RunnerFleetMonitor.ResetHysteresis();
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    // The path every existing deployment stays on. An installation with no runners is not a distributed
    // installation and must behave exactly as it always did.
    [Fact]
    public async Task WithNoRunnerRegistered_TheControlPlaneKeepsExecuting()
    {
        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(ReviewExecutionMode.InProcess, status.Mode);
        Assert.Null(status.Stall);
    }

    [Fact]
    public async Task WithOneActiveRunner_TheControlPlaneExecutesNothing()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow());

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(ReviewExecutionMode.RunnersOnly, status.Mode);
        Assert.Equal(1, status.ActiveRunnerCount);
    }

    // Every clause of the predicate is a way to be wrongly told there is capacity. A revoked runner that
    // keeps heartbeating would otherwise hold the whole installation in distributed mode forever.
    [Fact]
    public async Task ARevokedRunner_DoesNotCountAsCapacity()
    {
        var runner = await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow());
        runner.Revoke(this._time.GetUtcNow());
        await this._dbContext.SaveChangesAsync();

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(0, status.ActiveRunnerCount);
    }

    // Version skew during a rolling upgrade: a runner this build cannot serve is registered and healthy,
    // and counting it would keep the control plane idle while nothing could take the work.
    [Fact]
    public async Task ARunnerSpeakingAnUnservableContract_DoesNotCountAsCapacity()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow(), contractVersion: RunnerContractVersion.Current + 9);

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(0, status.ActiveRunnerCount);
    }

    // The hysteresis. A runner flapping around the heartbeat window must not toggle the execution mode on
    // every poll, so going quiet does not immediately hand the work back to the control plane.
    [Fact]
    public async Task AFleetThatJustWentQuiet_DoesNotImmediatelyReopenInProcessExecution()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow());
        Assert.Equal(ReviewExecutionMode.RunnersOnly, (await this.CreateMonitor().GetStatusAsync()).Mode);

        // Past the active window but well inside the settle period.
        this._time.Advance(TimeSpan.FromSeconds(150));

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(0, status.ActiveRunnerCount);
        Assert.Equal(ReviewExecutionMode.RunnersOnly, status.Mode);
    }

    [Fact]
    public async Task AFleetQuietForLongerThanTheSettlePeriod_HandsExecutionBack()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow());
        Assert.Equal(ReviewExecutionMode.RunnersOnly, (await this.CreateMonitor().GetStatusAsync()).Mode);

        this._time.Advance(TimeSpan.FromSeconds(150));
        await this.CreateMonitor().GetStatusAsync();
        this._time.Advance(TimeSpan.FromSeconds(400));

        Assert.Equal(ReviewExecutionMode.InProcess, (await this.CreateMonitor().GetStatusAsync()).Mode);
    }

    // A pending queue with an offline fleet is the failure this whole story exists to make loud.
    [Fact]
    public async Task PendingWorkAndNoActiveRunner_IsReportedAsAStallWithItsCause()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow().AddHours(-2));
        await this.AddPendingJobAsync(this._time.GetUtcNow().AddHours(-1));

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(ReviewExecutionMode.RunnersOnly, status.Mode);
        Assert.NotNull(status.Stall);
        Assert.Equal(QueueStallCause.NoActiveRunner, status.Stall!.Cause);
        Assert.Equal(1, status.Stall.PendingJobCount);
    }

    // Inside the grace period a quiet queue is just a quiet queue. Reporting every brief gap as a stall
    // would train an operator to ignore the condition.
    [Fact]
    public async Task PendingWorkInsideTheGracePeriod_IsNotYetAStall()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow().AddHours(-2));
        await this.AddPendingJobAsync(this._time.GetUtcNow().AddSeconds(-30));

        Assert.Null((await this.CreateMonitor().GetStatusAsync()).Stall);
    }

    // A tag nothing declares outlives the fleet coming back, so it is reported ahead of the fleet being
    // offline: an operator told only "no active runner" restarts runners and watches the jobs sit there.
    [Fact]
    public async Task WorkNoRunnerCanEverTake_IsReportedAsATagMismatchRatherThanAnOfflineFleet()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow().AddHours(-2));
        var clientId = await this.AddClientAsync(requiredTags: "gpu");
        await this.AddPendingJobAsync(this._time.GetUtcNow().AddHours(-1), clientId);

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(QueueStallCause.NoRunnerMatchesRequiredTags, status.Stall!.Cause);
        Assert.Contains("gpu", status.Stall.Detail, StringComparison.Ordinal);
    }

    // One tenant's runners must not stop another tenant's reviews. A job whose client no active runner can
    // serve stays this process's to run, or it would sit pending forever while the fleet looks healthy.
    [Fact]
    public async Task AClientNoActiveRunnerCanServe_IsStillThisProcesssToRun()
    {
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow(), tenantId: OtherTenantId);
        var strandedClient = await this.AddClientAsync();

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.Equal(ReviewExecutionMode.RunnersOnly, status.Mode);
        Assert.True(status.MayExecuteInProcess(strandedClient));
    }

    [Fact]
    public async Task AClientAnActiveRunnerCanServe_IsLeftToTheFleet()
    {
        var clientId = await this.AddClientAsync();
        await this.AddRunnerAsync(lastSeen: this._time.GetUtcNow());

        var status = await this.CreateMonitor().GetStatusAsync();

        Assert.False(status.MayExecuteInProcess(clientId));
    }

    // An installation the control plane is still serving cannot be stalled for want of a runner, or every
    // ordinary single-process deployment would start reporting one.
    [Fact]
    public async Task AnInstallationWithNoRunners_NeverReportsAStall()
    {
        await this.AddPendingJobAsync(this._time.GetUtcNow().AddHours(-5));

        Assert.Null((await this.CreateMonitor().GetStatusAsync()).Stall);
    }

    private RunnerFleetMonitor CreateMonitor()
    {
        return new RunnerFleetMonitor(
            this._dbContext,
            this._offers,
            Microsoft.Extensions.Options.Options.Create(new RunnerFleetOptions()),
            this._time);
    }

    private async Task<Guid> AddClientAsync(string requiredTags = "", Guid? tenantId = null)
    {
        var tenant = tenantId ?? TenantId;
        if (!await this._dbContext.Tenants.AnyAsync(t => t.Id == tenant))
        {
            this._dbContext.Tenants.Add(
                new TenantRecord
                {
                    Id = tenant,
                    Slug = $"t{tenant:N}",
                    DisplayName = $"tenant-{tenant:N}",
                });
        }

        var clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = tenant,
                DisplayName = $"client-{clientId:N}",
                IsActive = true,
                CreatedAt = this._time.GetUtcNow(),
                RequiredRunnerTags = requiredTags,
            });

        await this._dbContext.SaveChangesAsync();
        return clientId;
    }

    private async Task<ReviewRunner> AddRunnerAsync(DateTimeOffset lastSeen, int? contractVersion = null, Guid? tenantId = null)
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(),
            tenantId ?? TenantId,
            "runner-01",
            [],
            contractVersion ?? RunnerContractVersion.Current,
            "hashed:secret",
            $"LOOKUP{Guid.NewGuid():N}",
            this._time.GetUtcNow().AddDays(30),
            this._time.GetUtcNow());
        runner.MarkSeen(lastSeen);
        this._dbContext.ReviewRunners.Add(runner);
        await this._dbContext.SaveChangesAsync();
        return runner;
    }

    private async Task<ReviewJob> AddPendingJobAsync(DateTimeOffset submittedAt, Guid? clientId = null)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId ?? Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await this._repo.AddAsync(job);
        await this._dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE review_jobs SET submitted_at = {0} WHERE id = {1}",
            submittedAt,
            job.Id);
        return job;
    }
}
