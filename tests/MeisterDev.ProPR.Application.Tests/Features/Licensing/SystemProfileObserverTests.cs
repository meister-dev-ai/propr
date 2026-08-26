// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     Observing the system a licensed installation runs on. The profile is descriptive: it decides nothing
///     about what the installation is entitled to, and an observation that fails leaves the recorded profile
///     and the caller alone.
/// </summary>
public sealed class SystemProfileObserverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset IdentityCreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task TheFirstObservation_CapturesTheProfile()
    {
        var harness = new Harness();

        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        var captured = Assert.IsType<SystemProfileSnapshot>(harness.Store.Current);
        Assert.Equal("propr", captured.Stable.DatabaseName);
        Assert.Equal(16401, captured.Stable.DatabaseOid);
        Assert.Equal("7412345678901234567", captured.Stable.PostgresSystemIdentifier);
        Assert.Equal(IdentityCreatedAt.ToUnixTimeSeconds(), captured.Stable.IdentityCreatedAtUnixSeconds);
        Assert.Single(captured.Stable.ScmHostHashes!);
        Assert.Equal(Now, captured.CapturedAt);
        Assert.Equal(Now, captured.UpdatedAt);
        Assert.Empty(harness.Store.Drift);
    }

    [Fact]
    public async Task TheFirstObservation_RecordsNoDriftAndNamesTheObservingReplica()
    {
        var harness = new Harness();

        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Empty(harness.Store.Drift);
        Assert.Empty(harness.Log.Entries);
        Assert.Equal("replica-a", Assert.Single(harness.Store.Hostnames).Hostname);
    }

    // The sweep re-observes every quarter of an hour. An installation that has not moved has to keep reporting
    // the same profile, or every sweep would look like a change.
    [Fact]
    public async Task RepeatedObservationOfAnUnchangedInstallation_KeepsTheHashAndRecordsNothing()
    {
        var harness = new Harness();

        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        var captured = harness.Store.Current!.ProfileHash;

        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(captured, harness.Store.Current!.ProfileHash);
        Assert.Empty(harness.Store.Drift);
        Assert.Empty(harness.Log.Entries);
        Assert.Equal(Now, harness.Store.Current.CapturedAt);
        Assert.Equal(Now.AddHours(1), harness.Store.Current.UpdatedAt);
    }

    // Replicas of one installation differ in exactly these components, so treating them as identity would make
    // one installation report itself as drifting against itself.
    [Fact]
    public async Task AnObservationFromAnotherReplica_ChangesNoHashAndRecordsNoDrift()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        var captured = harness.Store.Current!.ProfileHash;

        harness.Time.Advance(TimeSpan.FromMinutes(15));
        harness.Environment.Components = harness.Environment.Components with
        {
            MachineName = "replica-b",
            ProcessorCount = 32,
            TotalAvailableMemoryBytes = 68_719_476_736,
        };
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(captured, harness.Store.Current!.ProfileHash);
        Assert.Empty(harness.Store.Drift);
        Assert.Equal("replica-b", harness.Store.Current.Volatile.MachineName);
        Assert.Equal(["replica-a", "replica-b"], harness.Store.Hostnames.Select(host => host.Hostname).Order());
    }

    [Fact]
    public async Task AChangedStableComponent_RecordsExactlyOneDriftRecordNamingWhatMoved()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        var captured = harness.Store.Current!.ProfileHash;

        harness.Time.Advance(TimeSpan.FromMinutes(15));
        harness.Database.Identity = harness.Database.Identity with { DatabaseName = "propr_restored" };
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        var drift = Assert.Single(harness.Store.Drift);
        Assert.Equal(Now.AddMinutes(15), drift.OccurredAt);
        Assert.Equal(["databaseName"], drift.ChangedComponents);
        Assert.Equal(captured, drift.PreviousHash);
        Assert.Equal(harness.Store.Current!.ProfileHash, drift.NewHash);
        Assert.NotEqual(captured, drift.NewHash);
    }

    [Fact]
    public async Task AChangedStableComponent_IsReportedOnceAtInformationLevel()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        harness.Database.Identity = harness.Database.Identity with { DatabaseName = "propr_restored" };
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        var entry = Assert.Single(harness.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Contains("databaseName", entry.Message, StringComparison.Ordinal);
    }

    // The change is recorded once for the installation, not once per replica that notices it.
    [Fact]
    public async Task AChangeAnotherReplicaAlreadyRecorded_RecordsNothingASecondTime()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        harness.Database.Identity = harness.Database.Identity with { DatabaseName = "propr_restored" };
        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Single(harness.Store.Drift);
    }

    // A role that may not read the cluster's system identifier has the component reported as absent, and the
    // profile hashes absence as such rather than leaving it out.
    [Fact]
    public async Task AClusterIdentifierTheRoleMayNotRead_IsRecordedAsAbsent()
    {
        var harness = new Harness();
        harness.Database.Identity = harness.Database.Identity with { SystemIdentifier = null };

        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Null(harness.Store.Current!.Stable.PostgresSystemIdentifier);

        var granting = new Harness();
        await granting.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.NotEqual(granting.Store.Current!.ProfileHash, harness.Store.Current.ProfileHash);
    }

    // The salt is the license identifier. Without one there is nothing to derive it from, and hashing with a
    // fixed salt would make the values comparable across licensees.
    [Fact]
    public async Task WithoutAVerifiedLicense_TheHostHashesAreAbsent()
    {
        var harness = new Harness();
        harness.LicenseState.State = LicenseState.None();

        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Null(harness.Store.Current!.Stable.ScmHostHashes);
    }

    // The salt is the license identifier, so renewing onto a new one recomputes every host hash even though the
    // installation is configured against exactly the same hosts. That is a real change to what the profile
    // reports and is recorded as one.
    [Fact]
    public async Task RenewingOntoANewLicenseId_RecordsOneChangeNamingTheHostHashes()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        var underFirstLicense = harness.Store.Current!.Stable.ScmHostHashes;

        harness.LicenseState.State = Harness.VerifiedStateFor("9d2c1e30-8a1f-4d20-b9a3-0c7f5a6e2d11");
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(["scmHostHashes"], Assert.Single(harness.Store.Drift).ChangedComponents);
        Assert.NotEqual(underFirstLicense, harness.Store.Current!.Stable.ScmHostHashes);
        Assert.Equal(harness.ScmHosts.HostBaseUrls.Count, harness.Store.Current.Stable.ScmHostHashes!.Count);
    }

    [Fact]
    public async Task ActivatingALicense_TurnsTheAbsentHostHashesIntoAValueAndRecordsTheChange()
    {
        var harness = new Harness();
        harness.LicenseState.State = LicenseState.None();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        harness.LicenseState.State = Harness.VerifiedStateFor("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05");
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(["scmHostHashes"], Assert.Single(harness.Store.Drift).ChangedComponents);
        Assert.Single(harness.Store.Current!.Stable.ScmHostHashes!);
    }

    // The profile grants nothing, so a probe that cannot answer is reported and left for the next observation.
    [Fact]
    public async Task AFailingProbe_IsReportedAndLeavesTheRecordedProfileAlone()
    {
        var harness = new Harness();
        await harness.Observer.ObserveAsync(IdentityCreatedAt);
        var captured = harness.Store.Current!.ProfileHash;

        harness.Database.Failure = new InvalidOperationException("the database went away");
        await harness.Observer.ObserveAsync(IdentityCreatedAt);

        Assert.Equal(captured, harness.Store.Current!.ProfileHash);
        Assert.Empty(harness.Store.Drift);
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.Log.Entries).LogLevel);
    }

    /// <summary>The observer over stores and probes a test can move, holding what each observation left behind.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            this.Time = new FakeTimeProvider(Now);
            this.Observer = new SystemProfileObserver(
                this.Store,
                this.Database,
                this.Environment,
                this.ScmHosts,
                this.LicenseState,
                this.Time,
                this.Log);
        }

        public StubDatabaseProbe Database { get; } = new();

        public StubEnvironmentProbe Environment { get; } = new();

        public ListLogger Log { get; } = new();

        public StubLicenseStateProvider LicenseState { get; } = new();

        public SystemProfileObserver Observer { get; }

        public StubScmHostSource ScmHosts { get; } = new();

        public InMemorySystemProfileStore Store { get; } = new();

        public FakeTimeProvider Time { get; }

        public static LicenseState VerifiedStateFor(string licenseId) => new()
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
        };
    }

    private sealed class InMemorySystemProfileStore : ISystemProfileStore
    {
        private readonly List<SystemProfileDrift> _drift = [];
        private readonly Dictionary<string, ReplicaHostname> _hostnames = new(StringComparer.Ordinal);

        public SystemProfileSnapshot? Current { get; private set; }

        public IReadOnlyList<SystemProfileDrift> Drift => this._drift;

        public IReadOnlyList<ReplicaHostname> Hostnames => this._hostnames.Values.ToList();

        public Task<SystemProfileSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Current);

        public Task<bool> TryCaptureAsync(SystemProfileSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (this.Current is not null)
            {
                return Task.FromResult(false);
            }

            this.Current = snapshot;

            return Task.FromResult(true);
        }

        public Task<bool> TryRecordChangeAsync(
            string expectedProfileHash,
            SystemProfileSnapshot snapshot,
            SystemProfileDrift drift,
            CancellationToken cancellationToken = default)
        {
            if (this.Current is null ||
                !string.Equals(this.Current.ProfileHash, expectedProfileHash, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            // The move and its record land together, as they do in the database.
            this.Current = snapshot;
            this._drift.Add(drift);

            return Task.FromResult(true);
        }

        public Task UpdateVolatileAsync(
            SystemProfileVolatileComponents components,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            if (this.Current is not null)
            {
                this.Current = this.Current with { Volatile = components, UpdatedAt = observedAt };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemProfileDrift>> ListDriftAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemProfileDrift>>(this._drift.OrderByDescending(entry => entry.OccurredAt).Take(limit).ToList());

        public Task RecordHostnameAsync(
            string hostname,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            this._hostnames[hostname] = this._hostnames.TryGetValue(hostname, out var existing)
                ? existing with { LastSeenAt = observedAt }
                : new ReplicaHostname(hostname, observedAt, observedAt);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReplicaHostname>> ListHostnamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Hostnames);
    }

    private sealed class StubDatabaseProbe : IDatabaseClusterIdentityProbe
    {
        public Exception? Failure { get; set; }

        public DatabaseClusterIdentity Identity { get; set; } =
            new("7412345678901234567", "propr", 16401);

        public Task<DatabaseClusterIdentity> ReadAsync(CancellationToken cancellationToken = default) =>
            this.Failure is null ? Task.FromResult(this.Identity) : throw this.Failure;
    }

    private sealed class StubEnvironmentProbe : ISystemProfileEnvironmentProbe
    {
        public SystemProfileVolatileComponents Components { get; set; } = new()
        {
            OperatingSystem = "Linux 6.1",
            Runtime = ".NET 10.0.0",
            ProcessorCount = 8,
            TotalAvailableMemoryBytes = 17_179_869_184,
            TimeZoneId = "Etc/UTC",
            MachineName = "replica-a",
        };

        public SystemProfileVolatileComponents Read() => this.Components;
    }

    private sealed class StubScmHostSource : IConfiguredScmHostSource
    {
        public IReadOnlyList<string> HostBaseUrls { get; set; } = ["https://dev.azure.com/northwind"];

        public Task<IReadOnlyList<string>> ListHostBaseUrlsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.HostBaseUrls);
    }

    private sealed class StubLicenseStateProvider : ILicenseStateProvider
    {
        public LicenseState State { get; set; } = Harness.VerifiedStateFor("f0a1a0d6-1f2b-4f7a-9c3f-2b4d6e8a1c05");

        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.State);

        public void Invalidate()
        {
        }
    }

    /// <summary>Keeps what was logged, which is what the one-line-per-change rule is asserted against.</summary>
    private sealed class ListLogger : ILogger<SystemProfileObserver>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message);
    }
}
