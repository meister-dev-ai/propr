// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The store behind the installation's observed profile. The upsert paths are what keep replicas observing
///     the same installation from recording one change several times, so they run against PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class SystemProfileRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    [Fact]
    public async Task TheCapturedProfile_IsReadBackComponentForComponent()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        Assert.True(await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now)));

        var stored = Assert.IsType<SystemProfileSnapshot>(await sut.GetCurrentAsync());
        Assert.Equal("7412345678901234567", stored.Stable.PostgresSystemIdentifier);
        Assert.Equal("propr", stored.Stable.DatabaseName);
        Assert.Equal(16401, stored.Stable.DatabaseOid);
        Assert.Equal(1755000000, stored.Stable.IdentityCreatedAtUnixSeconds);
        Assert.Equal(["1a", "2b"], stored.Stable.ScmHostHashes);
        Assert.Equal("Linux 6.1", stored.Volatile.OperatingSystem);
        Assert.Equal("replica-a", stored.Volatile.MachineName);
        Assert.Equal("hash-one", stored.ProfileHash);
        Assert.Equal(Now, stored.CapturedAt);
    }

    // Absence is part of the profile, so a component that was absent when it was captured has to read back as
    // absent rather than as a value the database chose.
    [Fact]
    public async Task AnAbsentComponent_IsReadBackAsAbsent()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        await sut.TryCaptureAsync(
            SnapshotWith(
                StableComponents() with { PostgresSystemIdentifier = null, ScmHostHashes = null },
                "hash-one",
                Now));

        var stored = await sut.GetCurrentAsync();
        Assert.Null(stored!.Stable.PostgresSystemIdentifier);
        Assert.Null(stored.Stable.ScmHostHashes);
    }

    // Replicas starting together each propose a baseline. One of them has to win, or the installation would
    // have no single profile to report.
    [Fact]
    public async Task ASecondCapture_LeavesTheFirstBaselineInPlace()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        Assert.True(await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now)));
        Assert.False(await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-two", Now.AddMinutes(1))));

        var stored = await sut.GetCurrentAsync();
        Assert.Equal("hash-one", stored!.ProfileHash);
        Assert.Equal(Now, stored.CapturedAt);
    }

    // Two replicas observing one change both try to replace the same row. The condition on the hash they read
    // is what makes exactly one of them the caller that goes on to record the change.
    [Fact]
    public async Task ReplacingOnTheObservedHash_SucceedsForOneCallerOnly()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        var replaced = await sut.TryRecordChangeAsync(
            "hash-one",
            SnapshotWith(StableComponents(), "hash-two", Now, Now.AddMinutes(15)),
            DriftFrom("hash-one", "hash-two", Now.AddMinutes(15)));
        var replacedAgain = await sut.TryRecordChangeAsync(
            "hash-one",
            SnapshotWith(StableComponents(), "hash-three", Now, Now.AddMinutes(15)),
            DriftFrom("hash-one", "hash-three", Now.AddMinutes(15)));

        Assert.True(replaced);
        Assert.False(replacedAgain);
        Assert.Equal("hash-two", (await sut.GetCurrentAsync())!.ProfileHash);
    }

    // The move and its record go in together, so a caller whose conditional update found the row already moved
    // must leave no record behind either.
    [Fact]
    public async Task ARefusedReplacement_RecordsNoChange()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        var replaced = await sut.TryRecordChangeAsync(
            "a-hash-the-row-does-not-carry",
            SnapshotWith(StableComponents(), "hash-two", Now, Now.AddMinutes(15)),
            DriftFrom("a-hash-the-row-does-not-carry", "hash-two", Now.AddMinutes(15)));

        Assert.False(replaced);
        Assert.Empty(await sut.ListDriftAsync(10));
        Assert.Equal("hash-one", (await sut.GetCurrentAsync())!.ProfileHash);
    }

    // A profile carrying a hash nothing accounts for cannot be explained afterwards, so the record has to be
    // visible the moment the move is.
    [Fact]
    public async Task AReplacedProfile_AndItsRecordAreVisibleTogether()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        await sut.TryRecordChangeAsync(
            "hash-one",
            SnapshotWith(StableComponents(), "hash-two", Now, Now.AddMinutes(15)),
            DriftFrom("hash-one", "hash-two", Now.AddMinutes(15)));

        // Read through a connection of its own, so what is asserted is what the transaction committed rather
        // than what the writing context is tracking.
        await using var reader = this.CreateContext();
        var readerStore = new SystemProfileRepository(reader);

        Assert.Equal("hash-two", (await readerStore.GetCurrentAsync())!.ProfileHash);
        var recorded = Assert.Single(await readerStore.ListDriftAsync(10));
        Assert.Equal("hash-one", recorded.PreviousHash);
        Assert.Equal("hash-two", recorded.NewHash);
    }

    // The move and its record are one unit. A record that cannot be written takes the move with it, so nothing
    // is left carrying a profile hash that no record accounts for.
    [Fact]
    public async Task ARecordThatCannotBeWritten_LeavesTheProfileWhereItWas()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        // The changed components are stored as jsonb, which cannot hold a NUL character. The profile update
        // carries no such value, so it succeeds and the drift insert that follows it is what fails.
        var unwritableComponent = "database" + (char)0 + "Name";
        var drift = DriftFrom("hash-one", "hash-two", Now.AddMinutes(15)) with
        {
            ChangedComponents = [unwritableComponent],
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => sut.TryRecordChangeAsync(
            "hash-one",
            SnapshotWith(StableComponents(), "hash-two", Now, Now.AddMinutes(15)),
            drift));

        await using var reader = this.CreateContext();
        var readerStore = new SystemProfileRepository(reader);

        Assert.Equal("hash-one", (await readerStore.GetCurrentAsync())!.ProfileHash);
        Assert.Empty(await readerStore.ListDriftAsync(10));
    }

    [Fact]
    public async Task ReplacingTheProfile_KeepsTheInstantItWasFirstCapturedAt()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        await sut.TryRecordChangeAsync(
            "hash-one",
            SnapshotWith(StableComponents(), "hash-two", Now, Now.AddDays(30)),
            DriftFrom("hash-one", "hash-two", Now.AddDays(30)));

        var stored = await sut.GetCurrentAsync();
        Assert.Equal(Now, stored!.CapturedAt);
        Assert.Equal(Now.AddDays(30), stored.UpdatedAt);
    }

    [Fact]
    public async Task UpdatingTheVolatileComponents_LeavesTheHashAndTheCapturedInstantAlone()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));

        await sut.UpdateVolatileAsync(
            VolatileComponents() with { MachineName = "replica-b", ProcessorCount = 32 },
            Now.AddMinutes(15));

        var stored = await sut.GetCurrentAsync();
        Assert.Equal("hash-one", stored!.ProfileHash);
        Assert.Equal(Now, stored.CapturedAt);
        Assert.Equal(Now.AddMinutes(15), stored.UpdatedAt);
        Assert.Equal("replica-b", stored.Volatile.MachineName);
        Assert.Equal(32, stored.Volatile.ProcessorCount);
        Assert.Equal("7412345678901234567", stored.Stable.PostgresSystemIdentifier);
    }

    [Fact]
    public async Task AnOlderVolatileObservation_DoesNotReplaceANewerOne()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-one", Now));
        await sut.UpdateVolatileAsync(VolatileComponents() with { MachineName = "replica-new" }, Now.AddMinutes(15));

        await sut.UpdateVolatileAsync(VolatileComponents() with { MachineName = "replica-old" }, Now.AddMinutes(5));

        var stored = await sut.GetCurrentAsync();
        Assert.Equal(Now.AddMinutes(15), stored!.UpdatedAt);
        Assert.Equal("replica-new", stored.Volatile.MachineName);
    }

    [Fact]
    public async Task TheRecordedChanges_ComeBackNewestFirstAndBounded()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-0", Now));

        for (var index = 0; index < 5; index++)
        {
            await sut.TryRecordChangeAsync(
                $"hash-{index}",
                SnapshotWith(StableComponents(), $"hash-{index + 1}", Now, Now.AddDays(index)),
                DriftFrom($"hash-{index}", $"hash-{index + 1}", Now.AddDays(index)));
        }

        var recorded = await sut.ListDriftAsync(3);

        Assert.Equal(3, recorded.Count);
        Assert.Equal(Now.AddDays(4), recorded[0].OccurredAt);
        Assert.Equal(Now.AddDays(2), recorded[2].OccurredAt);
        Assert.Equal(["databaseName"], recorded[0].ChangedComponents);
        Assert.Equal("hash-5", recorded[0].NewHash);
    }

    // Replica clocks disagree, so two records can carry the same instant. Their UUIDv7 tails are not insertion
    // counters, so the only promise is that all of the records can be read back.
    [Fact]
    public async Task RecordsSharingAnInstant_AllComeBack()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);
        await sut.TryCaptureAsync(SnapshotWith(StableComponents(), "hash-0", Now));

        for (var index = 0; index < 3; index++)
        {
            await sut.TryRecordChangeAsync(
                $"hash-{index}",
                SnapshotWith(StableComponents(), $"hash-{index + 1}", Now, Now),
                DriftFrom($"hash-{index}", $"hash-{index + 1}", Now));
        }

        var recorded = await sut.ListDriftAsync(10);

        Assert.Equal(["hash-1", "hash-2", "hash-3"], recorded.Select(entry => entry.NewHash).OrderBy(hash => hash));
    }

    // The host names are a set, so a replica seen again extends the window it has been seen over rather than
    // adding a row or resetting when it was first seen.
    [Fact]
    public async Task AHostNameSeenAgain_KeepsItsFirstSightingAndExtendsItsLast()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        await sut.RecordHostnameAsync("replica-a", Now);
        await sut.RecordHostnameAsync("replica-a", Now.AddHours(6));

        var hostname = Assert.Single(await sut.ListHostnamesAsync());
        Assert.Equal("replica-a", hostname.Hostname);
        Assert.Equal(Now, hostname.FirstSeenAt);
        Assert.Equal(Now.AddHours(6), hostname.LastSeenAt);
    }

    [Fact]
    public async Task AnOutOfOrderSighting_DoesNotMoveTheLastSeenInstantBackwards()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        await sut.RecordHostnameAsync("replica-a", Now.AddHours(6));
        await sut.RecordHostnameAsync("replica-a", Now);

        Assert.Equal(Now.AddHours(6), Assert.Single(await sut.ListHostnamesAsync()).LastSeenAt);
    }

    [Fact]
    public async Task TheHostNames_ComeBackMostRecentlySeenFirst()
    {
        await using var db = this.CreateContext();
        var sut = new SystemProfileRepository(db);

        await sut.RecordHostnameAsync("replica-a", Now);
        await sut.RecordHostnameAsync("replica-b", Now.AddHours(1));

        Assert.Equal(["replica-b", "replica-a"], (await sut.ListHostnamesAsync()).Select(host => host.Hostname));
    }

    private static SystemProfileStableComponents StableComponents() => new()
    {
        PostgresSystemIdentifier = "7412345678901234567",
        DatabaseName = "propr",
        DatabaseOid = 16401,
        IdentityCreatedAtUnixSeconds = 1755000000,
        ScmHostHashes = ["1a", "2b"],
    };

    private static SystemProfileVolatileComponents VolatileComponents() => new()
    {
        OperatingSystem = "Linux 6.1",
        Runtime = ".NET 10.0.0",
        ProcessorCount = 8,
        TotalAvailableMemoryBytes = 17_179_869_184,
        TimeZoneId = "Etc/UTC",
        MachineName = "replica-a",
    };

    private static SystemProfileDrift DriftFrom(string previousHash, string newHash, DateTimeOffset occurredAt) =>
        new()
        {
            OccurredAt = occurredAt,
            ChangedComponents = ["databaseName"],
            PreviousHash = previousHash,
            NewHash = newHash,
        };

    private static SystemProfileSnapshot SnapshotWith(
        SystemProfileStableComponents stable,
        string profileHash,
        DateTimeOffset capturedAt,
        DateTimeOffset? updatedAt = null) => new()
    {
        Stable = stable,
        Volatile = VolatileComponents(),
        ProfileHash = profileHash,
        CapturedAt = capturedAt,
        UpdatedAt = updatedAt ?? capturedAt,
    };

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreateContext();
        await db.LicensingSystemProfile.ExecuteDeleteAsync();
        await db.LicensingSystemProfileDrift.ExecuteDeleteAsync();
        await db.LicensingReplicaHostnames.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

/// <summary>
///     The whole observation over a real database: the probes read PostgreSQL, the configured hosts come out of
///     the connection table, and the result is what the installation persists.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class SystemProfileObservationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string LicenseId = "f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05";

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset IdentityCreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly List<string> _restrictedRoleNames = [];

    private readonly List<Guid> _seededClientIds = [];

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    // The hashes exist so two reports can be compared. What is stored must not let anyone read back which host
    // the installation is configured against.
    [Fact]
    public async Task ThePersistedProfile_CarriesNoPlainHost()
    {
        await using var db = this.CreateContext();
        await this.SeedConnectionAsync(db, "https://dev.azure.com/northwind");

        await this.CreateObserver(db).ObserveAsync(IdentityCreatedAt);

        var persisted = await this.ReadStableComponentsJsonAsync();
        Assert.DoesNotContain("dev.azure.com", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("northwind", persisted, StringComparison.OrdinalIgnoreCase);

        var stored = await new SystemProfileRepository(db).GetCurrentAsync();
        Assert.Equal(64, Assert.Single(stored!.Stable.ScmHostHashes!).Length);
    }

    [Fact]
    public async Task ThePostgresProbe_ReadsTheClusterIdentifierAndTheCurrentDatabase()
    {
        await using var db = this.CreateContext();

        var identity = await new PostgresClusterIdentityProbe(db, NullLogger<PostgresClusterIdentityProbe>.Instance)
            .ReadAsync();

        Assert.False(string.IsNullOrWhiteSpace(identity.SystemIdentifier));
        Assert.False(string.IsNullOrWhiteSpace(identity.DatabaseName));
        Assert.NotNull(identity.DatabaseOid);
    }

    // The cluster identifier is optional because an installation can take the privilege to read it away. The
    // function is executable by everyone on a stock cluster, so the test takes the privilege away to reach the
    // path where it is not. The database name and object identifier carry no such privilege and still come back.
    [Fact]
    public async Task ARoleThatMayNotReadTheClusterIdentifier_ReportsItAsAbsent()
    {
        var restrictedConnectionString = await this.TryCreateRestrictedRoleAsync();
        Skip.If(
            restrictedConnectionString is null,
            "Skipping because the fixture's database role may not create another role to read as.");

        await using var granting = this.CreateContext();

        // Cluster-wide for as long as the test holds it. A run killed inside this window against an external
        // long-lived database leaves the grant revoked; on the throwaway container the fixture starts, the
        // container goes with the run. Superusers bypass the check either way, so only this probe notices.
        await granting.Database.ExecuteSqlRawAsync("REVOKE EXECUTE ON FUNCTION pg_control_system() FROM PUBLIC");

        try
        {
            await using var db = CreateContext(restrictedConnectionString!);

            var identity = await new PostgresClusterIdentityProbe(db, NullLogger<PostgresClusterIdentityProbe>.Instance)
                .ReadAsync();

            Assert.Null(identity.SystemIdentifier);
            Assert.False(string.IsNullOrWhiteSpace(identity.DatabaseName));
            Assert.NotNull(identity.DatabaseOid);
        }
        finally
        {
            await granting.Database.ExecuteSqlRawAsync("GRANT EXECUTE ON FUNCTION pg_control_system() TO PUBLIC");
        }
    }

    /// <summary>
    ///     Creates a login role with no privileges beyond connecting, and returns a connection string for it.
    ///     Returns <see langword="null" /> when the fixture's own role may not create one.
    /// </summary>
    private async Task<string?> TryCreateRestrictedRoleAsync()
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var roleName = $"propr_profile_probe_{Guid.NewGuid():N}"[..40];

        await using var db = this.CreateContext();

        try
        {
            await db.Database.ExecuteSqlRawAsync($"""CREATE ROLE "{roleName}" LOGIN PASSWORD 'probe'""");
        }
        catch (PostgresException)
        {
            return null;
        }

        // Tracked before the grant, so a grant that fails still leaves the role to be cleaned up.
        this._restrictedRoleNames.Add(roleName);

        try
        {
            await db.Database.ExecuteSqlRawAsync($"""GRANT CONNECT ON DATABASE "{connectionStringBuilder.Database}" TO "{roleName}" """);
        }
        catch (PostgresException)
        {
            return null;
        }

        connectionStringBuilder.Username = roleName;
        connectionStringBuilder.Password = "probe";

        return connectionStringBuilder.ConnectionString;
    }

    [Fact]
    public async Task TheObservedProfile_IsTheSameOnEveryObservationOfAnUnchangedInstallation()
    {
        await using var db = this.CreateContext();
        await this.SeedConnectionAsync(db, "https://dev.azure.com/northwind");
        var observer = this.CreateObserver(db);
        var store = new SystemProfileRepository(db);

        await observer.ObserveAsync(IdentityCreatedAt);
        var captured = (await store.GetCurrentAsync())!.ProfileHash;

        await observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(captured, (await store.GetCurrentAsync())!.ProfileHash);
        Assert.Empty(await store.ListDriftAsync(10));
    }

    [Fact]
    public async Task ConfiguringAnotherHost_RecordsOneChangeNamingTheHostHashes()
    {
        await using var db = this.CreateContext();
        await this.SeedConnectionAsync(db, "https://dev.azure.com/northwind");
        var observer = this.CreateObserver(db);
        var store = new SystemProfileRepository(db);

        await observer.ObserveAsync(IdentityCreatedAt);
        await this.SeedConnectionAsync(db, "https://gitlab.example.com/platform");
        await observer.ObserveAsync(IdentityCreatedAt);

        var drift = Assert.Single(await store.ListDriftAsync(10));
        Assert.Equal(["scmHostHashes"], drift.ChangedComponents);
        Assert.Equal(2, (await store.GetCurrentAsync())!.Stable.ScmHostHashes!.Count);
    }

    private SystemProfileObserver CreateObserver(MeisterProPRDbContext db, string? licenseId = LicenseId)
    {
        return new SystemProfileObserver(
            new SystemProfileRepository(db),
            new PostgresClusterIdentityProbe(db, NullLogger<PostgresClusterIdentityProbe>.Instance),
            new StubEnvironmentProbe(),
            new ConfiguredScmHostRepository(db),
            new StubLicenseStateProvider(licenseId),
            new FakeTimeProvider(Now),
            NullLogger<SystemProfileObserver>.Instance);
    }

    private async Task<string> ReadStableComponentsJsonAsync()
    {
        await using var db = this.CreateContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT stable_components::text FROM licensing_system_profile WHERE id = 1";

            return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task SeedConnectionAsync(MeisterProPRDbContext db, string hostBaseUrl)
    {
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "System Profile Test Client",
            IsActive = true,
            CreatedAt = Now,
        };

        db.Clients.Add(client);
        db.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Provider = ScmProvider.AzureDevOps,
                HostBaseUrl = hostBaseUrl,
                AuthenticationKind = ScmAuthenticationKind.PersonalAccessToken,
                DisplayName = "System Profile Test Connection",
                EncryptedSecretMaterial = "not-a-secret",
                CreatedAt = Now,
                UpdatedAt = Now,
            });

        await db.SaveChangesAsync();
        this._seededClientIds.Add(client.Id);
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreateContext();
        await db.LicensingSystemProfile.ExecuteDeleteAsync();
        await db.LicensingSystemProfileDrift.ExecuteDeleteAsync();
        await db.LicensingReplicaHostnames.ExecuteDeleteAsync();

        if (this._seededClientIds.Count > 0)
        {
            await db.Clients.Where(client => this._seededClientIds.Contains(client.Id)).ExecuteDeleteAsync();
            this._seededClientIds.Clear();
        }

        await this.DropRestrictedRolesAsync(db);
    }

    private async Task DropRestrictedRolesAsync(MeisterProPRDbContext db)
    {
        var databaseName = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database;

        foreach (var roleName in this._restrictedRoleNames)
        {
            // The grant has to go before the role, because a role holding one cannot be dropped.
            await db.Database.ExecuteSqlRawAsync($"""REVOKE CONNECT ON DATABASE "{databaseName}" FROM "{roleName}" """);
            await db.Database.ExecuteSqlRawAsync($"""DROP ROLE IF EXISTS "{roleName}" """);
        }

        this._restrictedRoleNames.Clear();
    }

    private MeisterProPRDbContext CreateContext() => CreateContext(fixture.ConnectionString);

    private static MeisterProPRDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private sealed class StubEnvironmentProbe : ISystemProfileEnvironmentProbe
    {
        public SystemProfileVolatileComponents Read() => new()
        {
            OperatingSystem = "Linux 6.1",
            Runtime = ".NET 10.0.0",
            ProcessorCount = 8,
            TotalAvailableMemoryBytes = 17_179_869_184,
            TimeZoneId = "Etc/UTC",
            MachineName = "replica-a",
        };
    }

    private sealed class StubLicenseStateProvider(string? licenseId) : ILicenseStateProvider
    {
        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                licenseId is null
                    ? LicenseState.None()
                    : new LicenseState
                    {
                        Kind = LicenseStateKind.Verified,
                        Stage = LicenseStage.Active,
                        Claims = new LicenseClaims
                        {
                            LicenseId = licenseId,
                            Licensee = "Northwind Traders",
                            IssuedAt = Now.AddDays(-1),
                            NotBefore = Now.AddDays(-1),
                            ExpiresAt = Now.AddYears(1),
                            Capabilities = [],
                        },
                    });
        }

        public void Invalidate()
        {
        }
    }
}
