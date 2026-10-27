// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.PlatformAdminElevation;

/// <summary>
///     Stores elevations on a database of its own at the latest migration, so the foreign keys, the concurrency check
///     and the audit history behave as they do in an installation.
/// </summary>
[Collection("PostgresIntegration3")]
public sealed class PlatformAdminElevationServiceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _database = "elevations_" + Guid.NewGuid().ToString("N");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        await using (var migrations = Context(fixture.ConnectionString))
        {
            await fixture.CreateDatabaseAtMigrationAsync(_database, migrations.Database.GetMigrations().Last());
        }

        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = _database }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AnElevationGrantsItsScopeAndOnlyTheSystemTenantNeedsNone()
    {
        var seed = await this.SeedAsync();
        await using var db = Context(_connectionString);
        var service = this.Service(db);

        var started = await service.StartAsync(seed.Administrator, seed.Tenant, seed.ClientA, "TICKET-1", 30);
        Assert.Equal(StartElevationOutcome.Started, started.Outcome);

        var grants = await service.ResolveAsync(
            seed.Administrator,
            [seed.ClientA, seed.ClientB, seed.SystemClient, seed.OtherTenantClient]);
        Assert.Equal(ContentAccessKind.Elevation, grants.Single(grant => grant.ClientId == seed.ClientA).Kind);
        Assert.Equal(ContentAccessKind.Refused, grants.Single(grant => grant.ClientId == seed.ClientB).Kind);
        Assert.Equal(ContentAccessKind.Installation, grants.Single(grant => grant.ClientId == seed.SystemClient).Kind);
        Assert.Equal(ContentAccessKind.Refused, grants.Single(grant => grant.ClientId == seed.OtherTenantClient).Kind);

        var readable = await service.ResolveReadableAsync(seed.Administrator);
        Assert.Contains(readable, grant => grant.ClientId == seed.ClientA && grant.Kind == ContentAccessKind.Elevation);
        Assert.Contains(readable, grant => grant.ClientId == seed.SystemClient && grant.Kind == ContentAccessKind.Installation);
        Assert.DoesNotContain(readable, grant => grant.ClientId == seed.ClientB || grant.ClientId == seed.OtherTenantClient);

        var grant = grants.Single(candidate => candidate.ClientId == seed.ClientA);
        await service.RecordReadsAsync(
            seed.Administrator,
            [new ContentRead(grant.Elevation!, [grant], "the result of a review job", "GET /jobs/1/result", null)]);

        var entries = await db.TenantAuditEntries.AsNoTracking().Where(entry => entry.TenantId == seed.Tenant).ToListAsync();
        Assert.Equal(2, entries.Count(entry => entry.ElevationId == started.Elevation!.Id));
        Assert.Contains(entries, entry => entry.EventType == PlatformAdminElevationAuditEvents.ContentRead && entry.Detail!.Contains("TICKET-1"));
    }

    [Fact]
    public async Task TheExpiryOfAnElevationEndsItsGrantAndIsRecordedOnceAtTheExpiry()
    {
        var seed = await this.SeedAsync();
        await using var db = Context(_connectionString);
        var service = this.Service(db);
        var started = (await service.StartAsync(seed.Administrator, seed.Tenant, null, "TICKET-2", 10)).Elevation!;

        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(
            ContentAccessKind.Refused,
            Assert.Single(await service.ResolveAsync(seed.Administrator, [seed.ClientA])).Kind);
        Assert.Equal(1, await service.RecordExpiriesAsync());
        Assert.Equal(0, await service.RecordExpiriesAsync());
        var expiry = await db.TenantAuditEntries.AsNoTracking()
            .SingleAsync(entry => entry.ElevationId == started.Id && entry.EventType == PlatformAdminElevationAuditEvents.Expired);
        Assert.Equal(started.ExpiresAt, expiry.OccurredAt);
        Assert.Equal(PlatformAdminElevationState.Expired, (await service.GetAsync(started.Id))!.State);
    }

    [Fact]
    public async Task AnElevationEndedByOneRequestCannotBeEndedByAnotherThatReadItBefore()
    {
        var seed = await this.SeedAsync();
        await using var first = Context(_connectionString);
        await using var second = Context(_connectionString);
        var started = (await this.Service(first).StartAsync(seed.Administrator, seed.Tenant, null, "TICKET-3", 60)).Elevation!;
        var staleCopy = await second.PlatformAdminElevations.SingleAsync(elevation => elevation.Id == started.Id);

        Assert.Equal(EndElevationOutcome.Ended, (await this.Service(first).EndAsync(started.Id, seed.Administrator)).Outcome);

        staleCopy.EndedAt = _clock.GetUtcNow();
        staleCopy.ConcurrencyStamp = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(EndElevationOutcome.AlreadyOver, (await this.Service(first).EndAsync(started.Id, seed.Administrator)).Outcome);
    }

    private static MeisterProPRDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connectionString, o => o.UseVector()).Options);

    private PlatformAdminElevationService Service(MeisterProPRDbContext db) =>
        new(Microsoft.Extensions.Options.Options.Create(new PlatformAdminElevationOptions()), db, _clock);

    private async Task<Seed> SeedAsync()
    {
        await using var db = Context(_connectionString);
        var tenant = Tenant("acme");
        var otherTenant = Tenant("globex");
        db.Tenants.AddRange(tenant, otherTenant);
        if (!await db.Tenants.AnyAsync(row => row.Id == TenantCatalog.SystemTenantId))
        {
            db.Tenants.Add(new TenantRecord { Id = TenantCatalog.SystemTenantId, Slug = "system", DisplayName = "System" });
        }

        var clientA = Client(tenant.Id, "Client A");
        var clientB = Client(tenant.Id, "Client B");
        var systemClient = Client(TenantCatalog.SystemTenantId, "Installation client");
        var otherTenantClient = Client(otherTenant.Id, "Globex client");
        db.Clients.AddRange(clientA, clientB, systemClient, otherTenantClient);
        var administrator = new AppUserRecord
        {
            Id = Guid.NewGuid(),
            Username = "operator-" + Guid.NewGuid().ToString("N")[..8],
            GlobalRole = AppUserRole.Admin,
            IsActive = true,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.AppUsers.Add(administrator);
        await db.SaveChangesAsync();
        return new Seed(tenant.Id, clientA.Id, clientB.Id, systemClient.Id, otherTenantClient.Id, administrator.Id);
    }

    private TenantRecord Tenant(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = $"{slug}-{Guid.NewGuid():N}",
        DisplayName = slug,
        IsActive = true,
        CreatedAt = _clock.GetUtcNow(),
        UpdatedAt = _clock.GetUtcNow(),
    };

    private ClientRecord Client(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        DisplayName = name,
        IsActive = true,
        CreatedAt = _clock.GetUtcNow(),
    };

    private sealed record Seed(Guid Tenant, Guid ClientA, Guid ClientB, Guid SystemClient, Guid OtherTenantClient, Guid Administrator);
}
