// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

public sealed class LicensedResourceCountRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnEmptyInstallation_CountsNothing()
    {
        await using var db = CreateContext();

        var counts = await CreateRepository(db).GetCountsAsync();

        Assert.Equal(0, counts.Clients);
        Assert.Equal(0, counts.EnrolledRunners);
        Assert.Equal(0, counts.ReviewsInProgress);
    }

    // Every client row counts, active or not: a client that is switched off is still one the installation holds.
    [Fact]
    public async Task TheClientCount_IncludesEveryClientRow()
    {
        await using var db = CreateContext();
        db.Clients.Add(CreateClient("Active", isActive: true));
        db.Clients.Add(CreateClient("Inactive", isActive: false));
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db).GetCountsAsync();

        Assert.Equal(2, counts.Clients);
    }

    // Listing and deleting a client are both gated on the visibility rule, so an installation without
    // multi-tenancy cannot see or remove a client outside the System tenant. Counting one would hold it to a
    // number no action available to the operator can lower.
    [Fact]
    public async Task WithoutMultiTenancy_TheClientCountLeavesOutTheClientsTheInstallationCannotSee()
    {
        await using var db = CreateContext();
        db.Clients.Add(CreateClient("System", tenantId: TenantCatalog.SystemTenantId));
        db.Clients.Add(CreateClient("Untenanted", tenantId: Guid.Empty));
        db.Clients.Add(CreateClient("Another tenant", tenantId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db, multiTenancyAvailable: false).GetCountsAsync();

        Assert.Equal(2, counts.Clients);
    }

    // With the capability the installation holds every client it has, because every one of them is one it can
    // list and delete.
    [Fact]
    public async Task WithMultiTenancy_TheClientCountIncludesEveryTenantsClients()
    {
        await using var db = CreateContext();
        db.Clients.Add(CreateClient("System", tenantId: TenantCatalog.SystemTenantId));
        db.Clients.Add(CreateClient("Another tenant", tenantId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db, multiTenancyAvailable: true).GetCountsAsync();

        Assert.Equal(2, counts.Clients);
    }

    // A revoked runner can no longer be given work, so counting it would report an installation as holding
    // capacity it does not have.
    [Fact]
    public async Task TheRunnerCount_LeavesOutARevokedRunner()
    {
        await using var db = CreateContext();
        db.ReviewRunners.Add(CreateRunner("enrolled-one"));
        db.ReviewRunners.Add(CreateRunner("enrolled-two"));

        var revoked = CreateRunner("revoked");
        revoked.Revoke(Now);
        db.ReviewRunners.Add(revoked);
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db).GetCountsAsync();

        Assert.Equal(2, counts.EnrolledRunners);
    }

    // A credential lives only in the runner's own memory, so a host that was rescaled or restarted after its
    // credential expired cannot renew and enrolls again as a new row. The expired row fails authentication and
    // can never be given work, so counting it would spend a licensed place on a host that no longer exists.
    [Fact]
    public async Task TheRunnerCount_LeavesOutARunnerWhoseCredentialHasExpired()
    {
        await using var db = CreateContext();
        db.ReviewRunners.Add(CreateRunner("current", credentialExpiresAt: Now.AddDays(1)));
        db.ReviewRunners.Add(CreateRunner("expired", credentialExpiresAt: Now.AddSeconds(-1)));
        db.ReviewRunners.Add(CreateRunner("expiring-now", credentialExpiresAt: Now));
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db).GetCountsAsync();

        Assert.Equal(1, counts.EnrolledRunners);
    }

    // Renewal keeps the same row, so a host that renews before its credential runs out goes on occupying the
    // one place it already had.
    [Fact]
    public async Task ARenewedCredential_KeepsTheRunnerCounted()
    {
        await using var db = CreateContext();
        var runner = CreateRunner("renewing", credentialExpiresAt: Now.AddSeconds(-1));
        db.ReviewRunners.Add(runner);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await CreateRepository(db).GetCountsAsync()).EnrolledRunners);

        runner.RenewCredential("hash-renewed", "lookup-renewed", Now.AddDays(30), 1);
        await db.SaveChangesAsync();

        Assert.Equal(1, (await CreateRepository(db).GetCountsAsync()).EnrolledRunners);
    }

    // A limit on concurrent reviews is about the ones running at this instant, so a queued job and a finished
    // one are both outside it.
    [Fact]
    public async Task TheConcurrentReviewCount_CountsOnlyExecutingJobs()
    {
        await using var db = CreateContext();
        db.ReviewJobs.Add(CreateJob(JobStatus.Processing));
        db.ReviewJobs.Add(CreateJob(JobStatus.Processing));
        db.ReviewJobs.Add(CreateJob(JobStatus.Pending));
        db.ReviewJobs.Add(CreateJob(JobStatus.Completed));
        db.ReviewJobs.Add(CreateJob(JobStatus.Failed));
        await db.SaveChangesAsync();

        var counts = await CreateRepository(db).GetCountsAsync();

        Assert.Equal(2, counts.ReviewsInProgress);
    }

    internal static ClientRecord CreateClient(string displayName, bool isActive = true, Guid? tenantId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId ?? Guid.NewGuid(),
        DisplayName = displayName,
        IsActive = isActive,
        CreatedAt = Now,
    };

    internal static ReviewRunner CreateRunner(string displayName, DateTimeOffset? credentialExpiresAt = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        displayName,
        [],
        1,
        $"hash-{displayName}",
        $"lookup-{displayName}",
        credentialExpiresAt ?? Now.AddDays(30),
        Now);

    /// <summary>
    ///     The repository against a clock pinned to the instant the seeded rows are written for, so a credential
    ///     expiry is decided against a stated instant rather than against the host clock at test time.
    /// </summary>
    private static LicensedResourceCountRepository CreateRepository(
        MeisterProPRDbContext db,
        bool multiTenancyAvailable = true)
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>())
            .Returns(multiTenancyAvailable);

        return new LicensedResourceCountRepository(db, new FakeTimeProvider(Now), capabilities);
    }

    internal static ReviewJob CreateJob(JobStatus status)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.test/contoso",
            "project",
            "repository",
            1,
            1)
        {
            Status = status,
            SubmittedAt = Now,
        };

        return job;
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicensedResourceCounts_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

/// <summary>
///     The same counts over PostgreSQL, so the filters are known to translate to SQL rather than to run in
///     memory over rows the provider had to load first.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class LicensedResourceCountRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _jobIds = [];
    private readonly List<Guid> _runnerIds = [];

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Removes exactly the rows this test wrote. The container is shared across the collection, so
    ///     clearing the tables outright would take away state the other tests in it depend on.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.CreateContext();
        await db.ReviewJobs.Where(job => this._jobIds.Contains(job.Id)).ExecuteDeleteAsync();
        await db.ReviewRunners.Where(runner => this._runnerIds.Contains(runner.Id)).ExecuteDeleteAsync();
        await db.Clients.Where(client => this._clientIds.Contains(client.Id)).ExecuteDeleteAsync();
    }

    // The counts are asserted as the difference the seeded rows make, because the shared container carries
    // whatever the rest of the collection has written.
    [Fact]
    public async Task TheCounts_AnswerOverStoredRows()
    {
        var now = DateTimeOffset.UtcNow;

        await using var db = this.CreateContext();
        var before = await CreateRepository(db, now).GetCountsAsync();

        await using var seed = this.CreateContext();

        // The client points at the tenant every installation has, rather than one this test adds: a tenant it
        // created would have to be removed again, and removing one is not something this test is about.
        var client = LicensedResourceCountRepositoryTests.CreateClient("Contoso");
        client.TenantId = TenantCatalog.SystemTenantId;
        seed.Clients.Add(client);
        this._clientIds.Add(client.Id);

        var enrolled = LicensedResourceCountRepositoryTests.CreateRunner(
            "runner-one",
            credentialExpiresAt: now.AddDays(30));
        var revoked = LicensedResourceCountRepositoryTests.CreateRunner(
            "runner-two",
            credentialExpiresAt: now.AddDays(30));
        revoked.Revoke(now);
        var expired = LicensedResourceCountRepositoryTests.CreateRunner(
            "runner-three",
            credentialExpiresAt: now.AddMinutes(-1));
        seed.ReviewRunners.AddRange(enrolled, revoked, expired);
        this._runnerIds.AddRange([enrolled.Id, revoked.Id, expired.Id]);

        var processing = LicensedResourceCountRepositoryTests.CreateJob(JobStatus.Processing);
        var pending = LicensedResourceCountRepositoryTests.CreateJob(JobStatus.Pending);
        seed.ReviewJobs.AddRange(processing, pending);
        this._jobIds.AddRange([processing.Id, pending.Id]);

        await seed.SaveChangesAsync();

        await using var read = this.CreateContext();
        var after = await CreateRepository(read, now).GetCountsAsync();

        Assert.Equal(1, after.Clients - before.Clients);
        Assert.Equal(1, after.EnrolledRunners - before.EnrolledRunners);
        Assert.Equal(1, after.ReviewsInProgress - before.ReviewsInProgress);
    }

    /// <summary>The repository against a clock pinned to the instant the seeded rows are written for.</summary>
    private static LicensedResourceCountRepository CreateRepository(MeisterProPRDbContext db, DateTimeOffset now)
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>()).Returns(true);

        return new LicensedResourceCountRepository(db, new FakeTimeProvider(now), capabilities);
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
