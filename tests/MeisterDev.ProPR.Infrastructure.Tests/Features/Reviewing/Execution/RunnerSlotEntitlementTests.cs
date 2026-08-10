// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Slot metering at the lease boundary, including the exact count at which it stops. The interesting
///     cases are the two that a naive count gets wrong: a runner already inside the count asking for a
///     second job, and an installation that never opted into metering at all.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerSlotEntitlementTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;
    private readonly IRunnerLeaseOfferStore _offers = Substitute.For<IRunnerLeaseOfferStore>();

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.InstallationEditions.ExecuteDeleteAsync();
        this._repo = new JobRepository(this._dbContext, new TestDbContextFactory(this._options), NullLogger<JobRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    // An installation that never set a count is not metered. Reading "no value" as "no slots" would refuse
    // every lease on an install that never opted in, which is a worse failure than not metering it.
    [Fact]
    public async Task WithNoEntitledCountRecorded_LeasingIsNotMetered()
    {
        await this.SetEntitledSlotsAsync(null);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(99);

        var admission = await this.CreateEntitlement().AdmitAsync(Guid.NewGuid());

        Assert.Equal(RunnerLeaseRefusal.None, admission.Refusal);
    }

    // Zero is not "unmetered". Folding null and zero together made an explicit no-slots entitlement admit
    // every runner, which is the opposite of what setting it to zero asks for.
    [Fact]
    public async Task AnEntitlementOfZero_RefusesEveryRunner()
    {
        await this.SetEntitledSlotsAsync(0);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(0);

        var admission = await this.CreateEntitlement().AdmitAsync(Guid.NewGuid());

        Assert.Equal(RunnerLeaseRefusal.SlotLimitReached, admission.Refusal);
    }

    // Malformed rather than meaningful. Reading it as unmetered would let bad data silently switch
    // metering off, which is the one direction a storage mistake should never take.
    [Fact]
    public async Task ANegativeEntitlement_RefusesRatherThanDisablingMetering()
    {
        await this.SetEntitledSlotsAsync(-1);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(0);

        var admission = await this.CreateEntitlement().AdmitAsync(Guid.NewGuid());

        Assert.Equal(RunnerLeaseRefusal.SlotLimitReached, admission.Refusal);
    }

    [Theory]
    [InlineData(0, 3, RunnerLeaseRefusal.None)]
    [InlineData(2, 3, RunnerLeaseRefusal.None)]
    [InlineData(3, 3, RunnerLeaseRefusal.SlotLimitReached)]
    [InlineData(4, 3, RunnerLeaseRefusal.SlotLimitReached)]
    public async Task LeasingStopsExactlyAtTheEntitledCount(int held, int entitled, RunnerLeaseRefusal expected)
    {
        await this.SetEntitledSlotsAsync(entitled);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(held);

        var admission = await this.CreateEntitlement().AdmitAsync(Guid.NewGuid());

        Assert.Equal(expected, admission.Refusal);
    }

    // A runner already holding a lease is inside the count, so its next job is free. Refusing it would cap
    // every runner at one job however many slots the installation has, which meters throughput rather than
    // capacity.
    [Fact]
    public async Task ARunnerAlreadyHoldingALease_MayTakeAnotherJobAtTheLimit()
    {
        await this.SetEntitledSlotsAsync(1);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(1);
        var runnerId = Guid.NewGuid();
        await this.AddLeasedJobAsync(runnerId);

        var mine = await this.CreateEntitlement().AdmitAsync(runnerId);
        var other = await this.CreateEntitlement().AdmitAsync(Guid.NewGuid());

        Assert.Equal(RunnerLeaseRefusal.None, mine.Refusal);
        Assert.Equal(RunnerLeaseRefusal.SlotLimitReached, other.Refusal);
    }

    // Lowering the count below what is already held stops new leases without killing in-flight work: the
    // runners holding those leases are the ones still admitted.
    [Fact]
    public async Task LoweringTheCountBelowWhatIsHeld_StopsNewLeasesAndLeavesInFlightWorkAlone()
    {
        await this.SetEntitledSlotsAsync(1);
        this._offers.CountRunnersHoldingLeasesAsync(Arg.Any<CancellationToken>()).Returns(3);
        var holder = Guid.NewGuid();
        await this.AddLeasedJobAsync(holder);

        Assert.Equal(RunnerLeaseRefusal.None, (await this.CreateEntitlement().AdmitAsync(holder)).Refusal);
        Assert.Equal(RunnerLeaseRefusal.SlotLimitReached, (await this.CreateEntitlement().AdmitAsync(Guid.NewGuid())).Refusal);
    }

    [Fact]
    public async Task WithoutTheCapability_NoRunnerIsAdmittedAtAll()
    {
        await this.SetEntitledSlotsAsync(null);
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.DistributedExecution, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var admission = await this.CreateEntitlement(licensing).AdmitAsync(Guid.NewGuid());

        Assert.Equal(RunnerLeaseRefusal.NotLicensed, admission.Refusal);
        Assert.NotNull(admission.Detail);
    }

    // The catalog is what an operator sees; PremiumCapabilityKey.All is what the product calls canonical.
    // They had drifted, so a licensing page listed capabilities in a different order from the declared one.
    [Fact]
    public void TheCapabilityCatalog_ListsCapabilitiesInTheCanonicalOrder()
    {
        var catalogOrder = new StaticPremiumCapabilityCatalog().GetAll().Select(c => c.Key).ToArray();

        Assert.Equal(PremiumCapabilityKey.All, catalogOrder);
    }

    private RunnerSlotEntitlement CreateEntitlement(ILicensingCapabilityService? licensing = null)
    {
        return new RunnerSlotEntitlement(this._dbContext, this._offers, licensing);
    }

    private async Task SetEntitledSlotsAsync(int? entitled)
    {
        // Written as two shapes rather than a nullable parameter: EF's raw-SQL path has no store mapping
        // for DBNull, and a null int parameter would arrive as an untyped null the planner rejects.
        var sql = entitled is null
            ? """
              INSERT INTO installation_edition (id, edition, updated_at, entitled_runner_slots)
              VALUES (1, 1, now(), NULL)
              ON CONFLICT (id) DO UPDATE SET entitled_runner_slots = NULL
              """
            : """
              INSERT INTO installation_edition (id, edition, updated_at, entitled_runner_slots)
              VALUES (1, 1, now(), {0})
              ON CONFLICT (id) DO UPDATE SET entitled_runner_slots = EXCLUDED.entitled_runner_slots
              """;

        if (entitled is null)
        {
            await this._dbContext.Database.ExecuteSqlRawAsync(sql);
            return;
        }

        await this._dbContext.Database.ExecuteSqlRawAsync(sql, entitled.Value);
    }

    private async Task AddLeasedJobAsync(Guid runnerId)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await this._repo.AddAsync(job);
        await this._dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Processing', lease_owner = {0}, lease_generation = 1, lease_expires_at = now() + interval '5 minutes'
            WHERE id = {1}
            """,
            runnerId.ToString("D"),
            job.Id);
    }
}
