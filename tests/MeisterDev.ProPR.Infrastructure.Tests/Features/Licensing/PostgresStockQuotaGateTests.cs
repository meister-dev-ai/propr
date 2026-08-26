// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     Stock-quota admission against a real PostgreSQL instance. These have to run against the database,
///     because the gate relies on it rather than on the process to decide which of two simultaneous creations
///     gets the last place, and an in-memory double would prove nothing about that.
///     <para>
///         Every ceiling is derived from what the installation already holds, so the shared container can carry
///         whatever the rest of the collection has written.
///     </para>
/// </summary>
[Collection("PostgresIntegration")]
public sealed class PostgresStockQuotaGateTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string ClientsLockKey = "propr:quota:clients";
    private const string RunnersLockKey = "propr:quota:runners";

    /// <summary>
    ///     Bounds every admission the tests make. Without it a gate that waited on a lock it should not have
    ///     taken would hang the run instead of failing it.
    /// </summary>
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     The clock every count and every admission in this file reads, pinned to the instant the seeded rows
    ///     are written at. The runner count leaves out a runner whose credential has expired, so a seeded
    ///     credential dated from <see cref="Now" /> has to be judged against <see cref="Now" /> as well:
    ///     against the host clock the seeded runners would drop out of the count once that date passed, and the
    ///     runner ceilings would then fail for a reason outside the code under test.
    /// </summary>
    private static readonly TimeProvider Clock = new FakeTimeProvider(Now);

    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _runnerIds = [];
    private readonly List<Guid> _tenantIds = [];

    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Removes exactly the rows these tests wrote. The container is shared across the collection, so
    ///     clearing the tables outright would take away state the other tests in it depend on.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.CreateContext();
        await db.ReviewRunners.Where(runner => this._runnerIds.Contains(runner.Id)).ExecuteDeleteAsync();

        // Clients first: a tenant that still has one cannot be removed.
        await db.Clients.Where(client => this._clientIds.Contains(client.Id)).ExecuteDeleteAsync();
        await db.Tenants.Where(tenant => this._tenantIds.Contains(tenant.Id)).ExecuteDeleteAsync();
    }

    // The defect this rules out: a count read before the insert lets two creations at the last place both
    // pass. Under READ COMMITTED both read the state from before either wrote, and row locking does not
    // arbitrate them because they insert different rows.
    [Theory]
    [InlineData(LicenseLimitKey.Clients)]
    [InlineData(LicenseLimitKey.Runners)]
    public async Task AdmitOne_FromManyCreationsAtTheLastPlace_AdmitsExactlyOne(LicenseLimitKey key)
    {
        const int creators = 6;
        var ceiling = await this.CountAsync(key) + 1;
        var ids = Enumerable.Range(0, creators).Select(_ => this.TrackNew(key)).ToList();

        // Every claimant reports itself ready and then waits for one signal, so all six ask for the admission
        // together. Starting them one after another would let the first finish before the last began, and the
        // test would pass without the creations overlapping.
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = ids.Select(id => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == creators)
            {
                allArrived.SetResult();
            }

            await release.Task;
            return await this.TryCreateOneAsync(key, ceiling, id);
        })).ToList();

        await allArrived.Task.WaitAsync(AdmissionTimeout);
        release.SetResult();
        var admitted = await Task.WhenAll(attempts);

        Assert.Single(admitted, outcome => outcome);
        Assert.Equal(ceiling, await this.CountAsync(key));
    }

    // A refusal is quoted back to an operator, so it carries the number the installation is held to, the
    // number it was measured at, and which side of the licensing rules produced the first one.
    [Theory]
    [InlineData(LicenseLimitKey.Clients)]
    [InlineData(LicenseLimitKey.Runners)]
    public async Task AdmitOne_AtTheCeiling_RefusesAndCarriesTheCeilingTheCountAndTheSource(LicenseLimitKey key)
    {
        await this.SeedOneAsync(key);
        var held = await this.CountAsync(key);

        await using var db = this.CreateContext();
        await using var admission = await this.AdmitAsync(db, key, held);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(held, admission.Limit.Count);
        Assert.Equal(held, admission.CurrentCount);
        Assert.Equal(LicenseLimitSource.License, admission.Limit.Source);

        // Nothing was written, so the refusal ends the transaction it was decided in rather than making every
        // other creation wait while the caller reports it.
        Assert.Null(db.Database.CurrentTransaction);
        Assert.True(await this.CanTakeQuotaLockAsync(LockKeyFor(key)));
    }

    // The administration read and the refusal have to name one number. Both take the ceiling from the resolver
    // and the count from the count source, so the read reports the ceiling and the count the refusal quoted.
    // The license here states the number the installation already holds, so the next creation is the refused
    // one.
    [Fact]
    public async Task ARefusedCreation_QuotesTheCeilingAndTheCountTheAdministrationReadReports()
    {
        await this.SeedOneAsync(LicenseLimitKey.Clients);
        var held = await this.CountAsync(LicenseLimitKey.Clients);

        using var chain = LicenseTestChain.Create();
        using var anchor = chain.CreateAnchor();
        var verification = new LicenseVerifier(anchor).Verify(
            chain.Sign(
                LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with
                {
                    Limits = new LicenseLimits { Clients = LicenseLimit.Of(held) },
                }),
            Now);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        var stateProvider = Substitute.For<ILicenseStateProvider>();
        stateProvider.GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(LicenseState.Verified(verification.License, Now, Now.AddDays(-1), null));

        await using var db = this.CreateContext();
        var catalog = new StaticPremiumCapabilityCatalog();
        var capabilityService = new LicensingCapabilityService(
            catalog,
            new LicensingPolicyRepository(db, catalog),
            stateProvider,
            Substitute.For<ILicensingIdentityStore>());
        var resolver = new LicenseLimitResolver(stateProvider, capabilityService);

        var summary = await new GetLicensingSummaryHandler(
                capabilityService,
                new LicensedResourceCountRepository(db, Clock),
                resolver)
            .HandleAsync(new GetLicensingSummaryQuery());
        var reported = Assert.Single(summary.Limits!.Where(limit => limit.Key == LicenseLimitKey.Clients));

        using var timeout = new CancellationTokenSource(AdmissionTimeout);
        await using var admission = await new PostgresStockQuotaGate(db, resolver, Clock)
            .AdmitOneAsync(LicenseLimitKey.Clients, timeout.Token);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(reported.EffectiveCount, admission.Limit.Count);
        Assert.Equal(reported.EffectiveSource, admission.Limit.Source);
        Assert.Equal(reported.InformationalCount, admission.CurrentCount);
    }

    // An unlimited ceiling admits without serializing anything: a creation is not made to wait behind another
    // one, and nothing is counted. Proven by holding the quota's lock elsewhere, which an admission that took
    // it would wait on until the admission timeout cancelled it.
    [Fact]
    public async Task AdmitOne_UnderAnUnlimitedCeiling_AdmitsWithoutTakingTheLock()
    {
        await using var blocker = await this.HoldQuotaLockAsync(ClientsLockKey);

        try
        {
            await using var db = this.CreateContext();
            var gate = new PostgresStockQuotaGate(
                db,
                ResolverFor(
                    LicenseLimitResolution.Unlimited(
                        LicenseLimitKey.Clients,
                        LicenseLimitSource.License,
                        LicenseStage.Active)),
                Clock);

            using var timeout = new CancellationTokenSource(AdmissionTimeout);
            await using var admission = await gate.AdmitOneAsync(LicenseLimitKey.Clients, timeout.Token);

            Assert.True(admission.IsAdmitted);
            Assert.Null(admission.CurrentCount);
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally
        {
            await ReleaseQuotaLocksAsync(blocker);
        }
    }

    // The count is only meaningful under the lock, so the lock outlives the decision and is released once the
    // creation it admitted has been committed.
    [Fact]
    public async Task AnAdmission_HoldsTheQuotaLockUntilItIsCommitted()
    {
        var ceiling = await this.CountAsync(LicenseLimitKey.Clients) + 1;

        await using var db = this.CreateContext();
        await using var admission = await this.AdmitAsync(db, LicenseLimitKey.Clients, ceiling);

        Assert.True(admission.IsAdmitted);
        Assert.False(await this.CanTakeQuotaLockAsync(ClientsLockKey));

        await admission.CommitAsync();

        Assert.True(await this.CanTakeQuotaLockAsync(ClientsLockKey));
    }

    // The creation saves through the same context the gate opened its transaction on, so it is written inside
    // that transaction without the caller doing anything, and it becomes visible when the admission is
    // committed.
    [Fact]
    public async Task AnAdmittedCreation_BecomesVisibleWhenTheAdmissionIsCommitted()
    {
        var ceiling = await this.CountAsync(LicenseLimitKey.Clients) + 1;
        var client = this.CreateClient();

        await using var db = this.CreateContext();
        await using (var admission = await this.AdmitAsync(db, LicenseLimitKey.Clients, ceiling))
        {
            Assert.True(admission.IsAdmitted);

            db.Clients.Add(client);
            await db.SaveChangesAsync();
            Assert.False(await this.ClientExistsAsync(client.Id));

            await admission.CommitAsync();
        }

        Assert.True(await this.ClientExistsAsync(client.Id));
    }

    // Disposing an admission that was not committed ends its transaction, which discards the creation with it.
    // A caller that refuses the creation for its own reasons therefore leaves nothing behind.
    [Fact]
    public async Task AnAdmissionDisposedWithoutACommit_DiscardsTheCreation()
    {
        var ceiling = await this.CountAsync(LicenseLimitKey.Clients) + 1;
        var client = this.CreateClient();

        await using var db = this.CreateContext();
        await using (var admission = await this.AdmitAsync(db, LicenseLimitKey.Clients, ceiling))
        {
            Assert.True(admission.IsAdmitted);
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        Assert.False(await this.ClientExistsAsync(client.Id));
        Assert.True(await this.CanTakeQuotaLockAsync(ClientsLockKey));
    }

    // Live rows are counted every time rather than kept as a tally, so removing one frees its place with no
    // further action.
    [Theory]
    [InlineData(LicenseLimitKey.Clients)]
    [InlineData(LicenseLimitKey.Runners)]
    public async Task AdmitOne_AfterOneIsRemoved_AdmitsTheNextImmediately(LicenseLimitKey key)
    {
        var removable = await this.SeedOneAsync(key);
        var ceiling = await this.CountAsync(key);

        await using var refusing = this.CreateContext();
        await using (var refused = await this.AdmitAsync(refusing, key, ceiling))
        {
            Assert.False(refused.IsAdmitted);
        }

        await this.RemoveAsync(key, removable);

        await using var admitting = this.CreateContext();
        await using var admission = await this.AdmitAsync(admitting, key, ceiling);

        Assert.True(admission.IsAdmitted);
        Assert.Equal(ceiling - 1, admission.CurrentCount);
    }

    // A ceiling that drops below what the installation already holds only stops new creations. The rows above
    // it stay, because the decision compares the count against the ceiling and removes nothing.
    [Fact]
    public async Task AdmitOne_WithMoreHeldThanTheCeilingAllows_RefusesAndLeavesThemInPlace()
    {
        var seeded = new List<Guid>
        {
            await this.SeedOneAsync(LicenseLimitKey.Clients),
            await this.SeedOneAsync(LicenseLimitKey.Clients),
            await this.SeedOneAsync(LicenseLimitKey.Clients),
        };
        var held = await this.CountAsync(LicenseLimitKey.Clients);

        await using var db = this.CreateContext();
        await using var admission = await this.AdmitAsync(db, LicenseLimitKey.Clients, held - 2);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(held, admission.CurrentCount);
        Assert.Equal(held - 2, admission.Limit.Count);

        await using var read = this.CreateContext();
        Assert.Equal(seeded.Count, await read.Clients.CountAsync(client => seeded.Contains(client.Id)));
    }

    // Without a license the community values decide: an installation may configure any number of clients, and
    // it may enroll no runners at all. The resolver is the real one here, so the values the gate enforces are
    // the ones the rest of the licensing slice reports.
    [Fact]
    public async Task WithoutALicense_EveryClientIsAdmitted()
    {
        await using var db = this.CreateContext();
        using var timeout = new CancellationTokenSource(AdmissionTimeout);
        await using var admission = await new PostgresStockQuotaGate(db, UnlicensedResolver(), Clock)
            .AdmitOneAsync(LicenseLimitKey.Clients, timeout.Token);

        Assert.True(admission.IsAdmitted);
        Assert.Equal(LicenseLimitCeiling.Unlimited, admission.Limit.Ceiling);
        Assert.Equal(LicenseLimitSource.Community, admission.Limit.Source);
    }

    [Fact]
    public async Task WithoutALicense_NoRunnerIsAdmitted()
    {
        await using var db = this.CreateContext();
        using var timeout = new CancellationTokenSource(AdmissionTimeout);
        await using var admission = await new PostgresStockQuotaGate(db, UnlicensedResolver(), Clock)
            .AdmitOneAsync(LicenseLimitKey.Runners, timeout.Token);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(0, admission.Limit.Count);
        Assert.Equal(LicenseLimitSource.Community, admission.Limit.Source);
    }

    // A caller that opened its own transaction keeps it. The gate decides inside it, the admission commits
    // nothing, and the caller's own commit ends the transaction and releases the lock.
    [Fact]
    public async Task AdmitOne_InsideACallerOwnedTransaction_LeavesTheCommitToTheCaller()
    {
        var ceiling = await this.CountAsync(LicenseLimitKey.Clients) + 1;
        var client = this.CreateClient();

        await using var db = this.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await using var admission = await this.AdmitAsync(db, LicenseLimitKey.Clients, ceiling);

        Assert.True(admission.IsAdmitted);

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        await admission.CommitAsync();

        Assert.NotNull(db.Database.CurrentTransaction);
        Assert.False(await this.CanTakeQuotaLockAsync(ClientsLockKey));
        Assert.False(await this.ClientExistsAsync(client.Id));

        await transaction.CommitAsync();

        Assert.True(await this.ClientExistsAsync(client.Id));
        Assert.True(await this.CanTakeQuotaLockAsync(ClientsLockKey));
    }

    // The count a creation is admitted against is the clients the installation can list and delete. Without
    // multi-tenancy a client outside the System tenant is neither, so it holds no place against the ceiling;
    // refusing against it would name a number no action available to the operator can lower. With the
    // capability every client is one the installation holds, so the same row counts.
    [Fact]
    public async Task AdmitOne_CountsTheClientsTheInstallationCanSee()
    {
        await using var db = this.CreateContext();
        var beforeWithout = await this.CountThroughTheGateAsync(db, multiTenancyAvailable: false);
        var beforeWith = await this.CountThroughTheGateAsync(db, multiTenancyAvailable: true);

        await this.SeedClientInItsOwnTenantAsync();

        Assert.Equal(beforeWithout, await this.CountThroughTheGateAsync(db, multiTenancyAvailable: false));
        Assert.Equal(beforeWith + 1, await this.CountThroughTheGateAsync(db, multiTenancyAvailable: true));
    }

    // Only quotas counted as stored rows are admitted here. Reviews executing at once are bounded inside the
    // claim statement that starts one, and distinct authors within a month are not counted at all.
    [Theory]
    [InlineData(LicenseLimitKey.ConcurrentReviews)]
    [InlineData(LicenseLimitKey.AuthorsPerMonth)]
    public async Task AdmitOne_ForAQuotaThatIsNotACountOfRows_ThrowsForAnUnsupportedKey(LicenseLimitKey key)
    {
        await using var db = this.CreateContext();
        var gate = new PostgresStockQuotaGate(db, Substitute.For<ILicenseLimitResolver>(), Clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gate.AdmitOneAsync(key));
    }

    // The resolver answers the client and runner dimensions with an unlimited ceiling or a count, so a ceiling
    // no count can be compared against is a fault in the installation's state rather than in the key passed.
    [Fact]
    public async Task AdmitOne_WhenTheResolvedCeilingCannotBeCounted_ReportsAStateFault()
    {
        await using var db = this.CreateContext();
        var gate = new PostgresStockQuotaGate(
            db,
            ResolverFor(
                LicenseLimitResolution.Unmetered(
                    LicenseLimitKey.Clients,
                    LicenseLimitSource.License,
                    LicenseStage.Active)),
            Clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.AdmitOneAsync(LicenseLimitKey.Clients));
    }

    private static string LockKeyFor(LicenseLimitKey key) =>
        key == LicenseLimitKey.Clients ? ClientsLockKey : RunnersLockKey;

    private static ILicenseLimitResolver ResolverFor(LicenseLimitResolution resolution)
    {
        var resolver = Substitute.For<ILicenseLimitResolver>();
        resolver.ResolveAsync(resolution.Key, Arg.Any<CancellationToken>()).Returns(resolution);
        return resolver;
    }

    /// <summary>The product's own resolver over an installation with no license on file.</summary>
    private static ILicenseLimitResolver UnlicensedResolver()
    {
        var stateProvider = Substitute.For<ILicenseStateProvider>();
        stateProvider.GetStateAsync(Arg.Any<CancellationToken>()).Returns(LicenseState.None());

        return new LicenseLimitResolver(stateProvider, Substitute.For<ILicensingCapabilityService>());
    }

    private static async Task ReleaseQuotaLocksAsync(NpgsqlConnection connection)
    {
        // Released here rather than by closing the connection. Npgsql sends its session reset with the next
        // command on the pooled connection, so a connection that goes back to the pool unused keeps the lock
        // and the next admission waits on it.
        await using var release = connection.CreateCommand();
        release.CommandText = "SELECT pg_advisory_unlock_all()";
        await release.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     What the gate counts for the client quota, taken from an admission against a ceiling nothing can
    ///     reach. The admission is disposed without a commit, so it writes nothing and releases its lock.
    /// </summary>
    private async Task<long> CountThroughTheGateAsync(MeisterProPRDbContext db, bool multiTenancyAvailable)
    {
        var capabilities = Substitute.For<ILicensingCapabilityService>();
        capabilities.IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>())
            .Returns(multiTenancyAvailable);

        var gate = new PostgresStockQuotaGate(
            db,
            ResolverFor(
                LicenseLimitResolution.Of(
                    LicenseLimitKey.Clients,
                    long.MaxValue,
                    LicenseLimitSource.License,
                    LicenseStage.Active)),
            Clock,
            capabilities);

        using var timeout = new CancellationTokenSource(AdmissionTimeout);
        await using var admission = await gate.AdmitOneAsync(LicenseLimitKey.Clients, timeout.Token);

        Assert.True(admission.IsAdmitted);
        return admission.CurrentCount!.Value;
    }

    /// <summary>
    ///     A tenant of this test's own and one client in it, which is a client an installation without
    ///     multi-tenancy can neither list nor delete.
    /// </summary>
    private async Task SeedClientInItsOwnTenantAsync()
    {
        var tenant = new TenantRecord
        {
            Id = Guid.NewGuid(),
            Slug = $"quota-gate-{Guid.NewGuid():N}",
            DisplayName = "Quota gate tenant",
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        var client = this.CreateClient();
        client.TenantId = tenant.Id;

        await using var db = this.CreateContext();
        db.Tenants.Add(tenant);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        this._tenantIds.Add(tenant.Id);
    }

    private async Task<StockQuotaAdmission> AdmitAsync(
        MeisterProPRDbContext db,
        LicenseLimitKey key,
        long ceiling)
    {
        var gate = new PostgresStockQuotaGate(
            db,
            ResolverFor(LicenseLimitResolution.Of(key, ceiling, LicenseLimitSource.License, LicenseStage.Active)),
            Clock);

        using var timeout = new CancellationTokenSource(AdmissionTimeout);
        return await gate.AdmitOneAsync(key, timeout.Token);
    }

    /// <summary>
    ///     Admits one and creates it when admitted, the way a mutation site will: the creation saves through
    ///     the context the admission was decided on, and the admission is committed once it has.
    /// </summary>
    private async Task<bool> TryCreateOneAsync(LicenseLimitKey key, long ceiling, Guid id)
    {
        await using var db = this.CreateContext();
        await using var admission = await this.AdmitAsync(db, key, ceiling);
        if (!admission.IsAdmitted)
        {
            return false;
        }

        if (key == LicenseLimitKey.Clients)
        {
            db.Clients.Add(this.CreateClient(id));
        }
        else
        {
            db.ReviewRunners.Add(CreateRunner(id));
        }

        await db.SaveChangesAsync();
        await admission.CommitAsync();
        return true;
    }

    private async Task<long> CountAsync(LicenseLimitKey key)
    {
        await using var db = this.CreateContext();
        var counts = await new LicensedResourceCountRepository(db, Clock).GetCountsAsync();

        return key == LicenseLimitKey.Clients ? counts.Clients : counts.EnrolledRunners;
    }

    private async Task<Guid> SeedOneAsync(LicenseLimitKey key)
    {
        var id = this.TrackNew(key);

        await using var db = this.CreateContext();
        if (key == LicenseLimitKey.Clients)
        {
            db.Clients.Add(this.CreateClient(id));
        }
        else
        {
            db.ReviewRunners.Add(CreateRunner(id));
        }

        await db.SaveChangesAsync();
        return id;
    }

    private async Task RemoveAsync(LicenseLimitKey key, Guid id)
    {
        await using var db = this.CreateContext();
        if (key == LicenseLimitKey.Clients)
        {
            await db.Clients.Where(client => client.Id == id).ExecuteDeleteAsync();
        }
        else
        {
            await db.ReviewRunners.Where(runner => runner.Id == id).ExecuteDeleteAsync();
        }
    }

    private async Task<bool> ClientExistsAsync(Guid id)
    {
        await using var db = this.CreateContext();
        return await db.Clients.AsNoTracking().AnyAsync(client => client.Id == id);
    }

    /// <summary>
    ///     Whether one quota's admission lock is free, asked from a connection of its own. The probe takes the
    ///     lock for the statement that asks, so it reports what it found without keeping it.
    /// </summary>
    private async Task<bool> CanTakeQuotaLockAsync(string lockKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))";
        probe.Parameters.AddWithValue("key", lockKey);
        var free = (bool)(await probe.ExecuteScalarAsync())!;

        await ReleaseQuotaLocksAsync(connection);
        return free;
    }

    /// <summary>Holds one quota's admission lock for as long as the returned connection is open.</summary>
    private async Task<NpgsqlConnection> HoldQuotaLockAsync(string lockKey)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // Session-scoped, so it is held until it is released, and it contends with the transaction-scoped lock
        // an admission takes because both live in the same advisory lock space.
        await using var hold = connection.CreateCommand();
        hold.CommandText = "SELECT pg_advisory_lock(hashtextextended(@key, 0))";
        hold.Parameters.AddWithValue("key", lockKey);
        await hold.ExecuteNonQueryAsync();

        return connection;
    }

    /// <summary>Records an identifier for removal and returns it, so a failed test leaves no rows behind.</summary>
    private Guid TrackNew(LicenseLimitKey key)
    {
        var id = Guid.NewGuid();
        if (key == LicenseLimitKey.Clients)
        {
            this._clientIds.Add(id);
        }
        else
        {
            this._runnerIds.Add(id);
        }

        return id;
    }

    /// <summary>
    ///     A client under the tenant every installation has. One this test created would have to be removed
    ///     again, and removing a tenant is not something these tests are about.
    /// </summary>
    private ClientRecord CreateClient(Guid? id = null) => new()
    {
        Id = id ?? this.TrackNew(LicenseLimitKey.Clients),
        TenantId = TenantCatalog.SystemTenantId,
        DisplayName = $"quota-gate-{Guid.NewGuid():N}",
        IsActive = true,
        CreatedAt = Now,
    };

    private static ReviewRunner CreateRunner(Guid id) => new(
        id,
        Guid.NewGuid(),
        $"quota-gate-{id:N}",
        [],
        1,
        $"hash-{id:N}",
        $"lookup-{id:N}",
        Now.AddDays(30),
        Now);

    private MeisterProPRDbContext CreateContext() => new(this._options);
}
