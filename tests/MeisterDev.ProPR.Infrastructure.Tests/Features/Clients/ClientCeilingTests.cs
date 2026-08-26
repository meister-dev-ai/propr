// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Clients;

/// <summary>
///     Client creation against the licensed client ceiling, through the admin service and a real gate over a
///     real PostgreSQL instance. The gate decides in the database, so a double would prove nothing about what
///     the service does at the ceiling.
///     <para>
///         Every ceiling here is derived from what the installation already holds, so the shared container can
///         carry whatever the rest of the collection has written.
///     </para>
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClientCeilingTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     Bounds every creation the tests make. Without it a creation that waited on a lock it should not have
    ///     taken would hang the run instead of failing it.
    /// </summary>
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromSeconds(10);

    private readonly List<Guid> _clientIds = [];

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
    ///     clearing the table outright would take away state the other tests in it depend on.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.CreateContext();
        await db.Clients.Where(client => this._clientIds.Contains(client.Id)).ExecuteDeleteAsync();
    }

    // The refusal is quoted back to an operator, so it names the number the installation is held to and the
    // number it holds. Nothing is written, so the count is unchanged.
    //
    // Two clients are seeded so both numbers are above one, which fixes the plural form the message takes and
    // lets the expected text be written out. The singular form is covered without a database below.
    [Fact]
    public async Task CreateAsync_AtTheLicensedCeiling_IsRefusedAndPersistsNothing()
    {
        this.TrackNew(await this.SeedClientAsync());
        this.TrackNew(await this.SeedClientAsync());
        var held = await this.CountClientsAsync();

        await using var db = this.CreateContext();
        var service = CreateService(db, LicenseLimitResolution.Of(LicenseLimitKey.Clients, held, LicenseLimitSource.License, LicenseStage.Active));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateOneAsync(service, "ceiling-reached"));

        Assert.Equal(
            $"The license in force allows {held} clients and {held} exist. "
            + "Creating another requires removing a client, or a license that allows more.",
            refusal.Message);
        Assert.Equal(held, await this.CountClientsAsync());
    }

    // A ceiling that drops below what the installation already holds stops new creations and removes nothing.
    // The two numbers in the refusal differ here, so the test can tell them apart. Four clients are seeded so
    // that the lowered ceiling is still above one, which fixes the plural form the message takes.
    [Fact]
    public async Task CreateAsync_WithMoreClientsThanALoweredCeilingAllows_NamesTheCeilingAndTheCount()
    {
        var seeded = new List<Guid>
        {
            this.TrackNew(await this.SeedClientAsync()),
            this.TrackNew(await this.SeedClientAsync()),
            this.TrackNew(await this.SeedClientAsync()),
            this.TrackNew(await this.SeedClientAsync()),
        };
        var held = await this.CountClientsAsync();
        var ceiling = held - 2;

        await using var db = this.CreateContext();
        var service = CreateService(db, LicenseLimitResolution.Of(LicenseLimitKey.Clients, ceiling, LicenseLimitSource.License, LicenseStage.Active));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateOneAsync(service, "ceiling-lowered"));

        Assert.Equal(
            $"The license in force allows {ceiling} clients and {held} exist. "
            + "Creating another requires removing a client, or a license that allows more.",
            refusal.Message);

        await using var read = this.CreateContext();
        Assert.Equal(seeded.Count, await read.Clients.CountAsync(client => seeded.Contains(client.Id)));
    }

    // Below the ceiling the creation is committed rather than left in the admission's transaction, so a reader
    // on another connection sees it and the lock the admission held is released.
    [Fact]
    public async Task CreateAsync_BelowTheCeiling_CommitsTheCreation()
    {
        var ceiling = await this.CountClientsAsync() + 1;

        await using var db = this.CreateContext();
        var service = CreateService(db, LicenseLimitResolution.Of(LicenseLimitKey.Clients, ceiling, LicenseLimitSource.License, LicenseStage.Active));

        var created = await this.CreateOneAsync(service, "below-ceiling");
        this.TrackNew(created.Id);

        Assert.Null(db.Database.CurrentTransaction);
        Assert.True(await this.ClientExistsAsync(created.Id));
        Assert.Equal(ceiling, await this.CountClientsAsync());
    }

    // Live rows are counted on every decision, so removing a client frees its place with no further action.
    [Fact]
    public async Task CreateAsync_AfterAClientIsDeleted_IsAdmittedAgain()
    {
        var removable = this.TrackNew(await this.SeedClientAsync());
        var ceiling = await this.CountClientsAsync();
        var limit = LicenseLimitResolution.Of(LicenseLimitKey.Clients, ceiling, LicenseLimitSource.License, LicenseStage.Active);

        await using (var refusing = this.CreateContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateOneAsync(CreateService(refusing, limit), "before-delete"));
        }

        await using var deleting = this.CreateContext();
        Assert.True(await CreateService(deleting, limit).DeleteAsync(removable, CancellationToken.None));

        await using var admitting = this.CreateContext();
        var created = await this.CreateOneAsync(CreateService(admitting, limit), "after-delete");
        this.TrackNew(created.Id);

        Assert.True(await this.ClientExistsAsync(created.Id));
    }

    // Without a license the client ceiling is unlimited, so nothing is counted and no lock is taken. An
    // unlicensed installation therefore creates clients as it did before the ceiling was enforced.
    [Fact]
    public async Task CreateAsync_UnderAnUnlimitedCeiling_CreatesTheClient()
    {
        await using var db = this.CreateContext();
        var service = CreateService(
            db,
            LicenseLimitResolution.Unlimited(LicenseLimitKey.Clients, LicenseLimitSource.Community, LicenseStage.None));

        var created = await this.CreateOneAsync(service, "unlimited");
        this.TrackNew(created.Id);

        Assert.True(await this.ClientExistsAsync(created.Id));
    }

    // The defect this rules out: a count read before the insert lets two creations at the last place both pass.
    // The gate's own tests prove the arbitration. This one proves the service creates through the gate, so the
    // creation that loses is refused rather than saved.
    [Fact]
    public async Task CreateAsync_FromTwoSimultaneousCreationsAtTheLastPlace_CreatesExactlyOne()
    {
        // One client is seeded so the ceiling is above one, which fixes the plural form the refusal takes.
        this.TrackNew(await this.SeedClientAsync());
        var ceiling = await this.CountClientsAsync() + 1;
        var limit = LicenseLimitResolution.Of(LicenseLimitKey.Clients, ceiling, LicenseLimitSource.License, LicenseStage.Active);

        // The creation that loses decides after the winner has committed, so it counts the ceiling exactly.
        var expectedRefusal =
            $"The license in force allows {ceiling} clients and {ceiling} exist. "
            + "Creating another requires removing a client, or a license that allows more.";

        // Both creators report themselves ready and then wait for one signal, so they ask for the admission
        // together. Starting them one after another would let the first finish before the second began.
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run<(ClientDto? Created, string? Refusal)>(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                allArrived.SetResult();
            }

            await release.Task;

            await using var db = this.CreateContext();
            try
            {
                return (await this.CreateOneAsync(CreateService(db, limit), $"race-{index}"), null);
            }
            catch (InvalidOperationException refusal)
                when (string.Equals(refusal.Message, expectedRefusal, StringComparison.Ordinal))
            {
                // Only the ceiling refusal is an outcome here. A state fault from the gate or a failed read
                // back carries a different message, and it leaves the test rather than counting as no
                // creation.
                return (null, refusal.Message);
            }
        })).ToList();

        await allArrived.Task.WaitAsync(CreationTimeout);
        release.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        var created = Assert.Single(outcomes, outcome => outcome.Created is not null).Created;
        this.TrackNew(created!.Id);

        var refused = Assert.Single(outcomes, outcome => outcome.Refusal is not null);
        Assert.Equal(expectedRefusal, refused.Refusal);
        Assert.Equal(ceiling, await this.CountClientsAsync());
    }

    /// <summary>
    ///     The admin service over one context, with the product's own gate deciding against a fixed ceiling.
    ///     The gate opens its transaction on this context, which is the one the creation saves through.
    /// </summary>
    private static ClientAdminService CreateService(MeisterProPRDbContext db, LicenseLimitResolution limit)
    {
        var limits = Substitute.For<ILicenseLimitResolver>();
        limits.ResolveAsync(limit.Key, Arg.Any<CancellationToken>()).Returns(limit);

        return new ClientAdminService(db, new PostgresStockQuotaGate(db, limits, TimeProvider.System));
    }

    private async Task<ClientDto> CreateOneAsync(ClientAdminService service, string label)
    {
        using var timeout = new CancellationTokenSource(CreationTimeout);
        return await service.CreateAsync(
            TenantCatalog.SystemTenantId,
            $"client-ceiling-{label}-{Guid.NewGuid():N}",
            timeout.Token);
    }

    private async Task<long> CountClientsAsync()
    {
        await using var db = this.CreateContext();
        return await db.Clients.AsNoTracking().LongCountAsync();
    }

    private async Task<bool> ClientExistsAsync(Guid id)
    {
        await using var db = this.CreateContext();
        return await db.Clients.AsNoTracking().AnyAsync(client => client.Id == id);
    }

    /// <summary>
    ///     A client written straight to the table, under the tenant every installation has. Seeding through the
    ///     service would need an admission of its own, and these tests measure the admission.
    /// </summary>
    private async Task<Guid> SeedClientAsync()
    {
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = $"client-ceiling-seed-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var db = this.CreateContext();
        db.Clients.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    /// <summary>Records an identifier for removal and returns it, so a failed test leaves no rows behind.</summary>
    private Guid TrackNew(Guid id)
    {
        this._clientIds.Add(id);
        return id;
    }

    private MeisterProPRDbContext CreateContext() => new(this._options);
}

/// <summary>
///     The wording of the client-ceiling refusal. The refusal is decided before anything is read or written, so
///     these need no database and cover the numbers the ceiling tests above cannot fix.
/// </summary>
public sealed class ClientCeilingRefusalTests
{
    // A ceiling of one and a count of one each take a singular noun, and every higher number takes a plural
    // one. The two agree independently, so a mixed pair is covered as well.
    [Theory]
    [InlineData(1, 1, "1 client", "1 exists")]
    [InlineData(1, 2, "1 client", "2 exist")]
    [InlineData(3, 3, "3 clients", "3 exist")]
    public async Task CreateAsync_WhenRefused_AgreesEachNounWithItsNumber(
        long ceiling,
        long held,
        string allowed,
        string counted)
    {
        await using var db = CreateContext();
        var service = new ClientAdminService(db, RefusingGate(ceiling, held));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            TenantCatalog.SystemTenantId, "refused", CancellationToken.None));

        Assert.Equal(
            $"The license in force allows {allowed} and {counted}. "
            + "Creating another requires removing a client, or a license that allows more.",
            refusal.Message);
    }

    private static IStockQuotaGate RefusingGate(long ceiling, long held)
    {
        var admission = StockQuotaAdmission.Refused(
            LicenseLimitResolution.Of(LicenseLimitKey.Clients, ceiling, LicenseLimitSource.License, LicenseStage.Active),
            held);

        var gate = Substitute.For<IStockQuotaGate>();
        gate.AdmitOneAsync(LicenseLimitKey.Clients, Arg.Any<CancellationToken>()).Returns(admission);
        return gate;
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ClientCeilingRefusalTests-{Guid.NewGuid():N}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
