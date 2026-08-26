// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed store for the installation's observed system profile.</summary>
public sealed class SystemProfileRepository(MeisterProPRDbContext dbContext) : ISystemProfileStore
{
    private const int SingletonProfileId = 1;
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<SystemProfileSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var record = await dbContext.LicensingSystemProfile
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == SingletonProfileId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : ToSnapshot(record);
    }

    /// <inheritdoc />
    public async Task<bool> TryCaptureAsync(
        SystemProfileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var stable = Serialize(snapshot.Stable);
        var volatileComponents = Serialize(snapshot.Volatile);

        if (this.IsPostgres())
        {
            // Replicas starting together each propose a profile. They observe the same installation, so the
            // conflict clause keeping the first insert leaves one baseline rather than a contested one.
            var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO licensing_system_profile
                         (id, stable_components, volatile_components, profile_hash, captured_at, updated_at)
                     VALUES ({SingletonProfileId}, {stable}::jsonb, {volatileComponents}::jsonb,
                             {snapshot.ProfileHash}, {snapshot.CapturedAt}, {snapshot.UpdatedAt})
                     ON CONFLICT (id) DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            return inserted == 1;
        }

        if (await dbContext.LicensingSystemProfile
                .AnyAsync(row => row.Id == SingletonProfileId, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        dbContext.LicensingSystemProfile.Add(
            new LicensingSystemProfileRecord
            {
                Id = SingletonProfileId,
                StableComponents = stable,
                VolatileComponents = volatileComponents,
                ProfileHash = snapshot.ProfileHash,
                CapturedAt = snapshot.CapturedAt,
                UpdatedAt = snapshot.UpdatedAt,
            });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordChangeAsync(
        string expectedProfileHash,
        SystemProfileSnapshot snapshot,
        SystemProfileDrift drift,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(drift);

        // The drift record must describe the same transition the profile update makes. A caller that supplied
        // three independent hashes could persist a profile move beside a drift record describing a different
        // one, so the method enforces consistency here rather than relying on caller discipline.
        if (!string.Equals(drift.PreviousHash, expectedProfileHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The drift's previous hash must match the expected profile hash.", nameof(drift));
        }

        if (!string.Equals(drift.NewHash, snapshot.ProfileHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The drift's new hash must match the snapshot's profile hash.", nameof(drift));
        }

        var stable = Serialize(snapshot.Stable);
        var volatileComponents = Serialize(snapshot.Volatile);

        if (!this.IsPostgres())
        {
            return await this
                .RecordChangeWithoutTransactionAsync(
                    expectedProfileHash,
                    snapshot,
                    drift,
                    stable,
                    volatileComponents,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The move and its record are written together. Either both are written or neither is, so the profile
        // cannot come out of an interruption carrying a hash that nothing accounts for.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // The hash the caller observed is part of the condition, so exactly one of several replicas seeing
        // the same change updates the row, and that is the one that records it.
        var replaced = await dbContext.LicensingSystemProfile
            .Where(row => row.Id == SingletonProfileId && row.ProfileHash == expectedProfileHash)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(record => record.StableComponents, stable)
                    .SetProperty(record => record.VolatileComponents, volatileComponents)
                    .SetProperty(record => record.ProfileHash, snapshot.ProfileHash)

                    // The later of the two instants, because the observing replica reads its own host clock
                    // and a replica whose clock is behind would otherwise report the row as last observed
                    // before an observation that had already been recorded. The condition on the hash decides
                    // whether the change is applied; only the instant is held to the row's own value.
                    .SetProperty(
                        record => record.UpdatedAt,
                        record => record.UpdatedAt > snapshot.UpdatedAt ? record.UpdatedAt : snapshot.UpdatedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (replaced != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        dbContext.LicensingSystemProfileDrift.Add(ToRecord(drift));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    ///     Replaces the profile and records the change on providers without an upsert, which is the in-memory
    ///     test host. One save covers both writes, so the record accompanies the move here as well.
    /// </summary>
    private async Task<bool> RecordChangeWithoutTransactionAsync(
        string expectedProfileHash,
        SystemProfileSnapshot snapshot,
        SystemProfileDrift drift,
        string stable,
        string volatileComponents,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.LicensingSystemProfile
            .SingleOrDefaultAsync(row => row.Id == SingletonProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null || !string.Equals(record.ProfileHash, expectedProfileHash, StringComparison.Ordinal))
        {
            return false;
        }

        record.StableComponents = stable;
        record.VolatileComponents = volatileComponents;
        record.ProfileHash = snapshot.ProfileHash;

        // Held to the later of the two instants for the same reason as the statement above.
        if (record.UpdatedAt < snapshot.UpdatedAt)
        {
            record.UpdatedAt = snapshot.UpdatedAt;
        }

        dbContext.LicensingSystemProfileDrift.Add(ToRecord(drift));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task UpdateVolatileAsync(
        SystemProfileVolatileComponents components,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        var serialized = Serialize(components);

        if (this.IsPostgres())
        {
            await dbContext.LicensingSystemProfile
                .Where(row => row.Id == SingletonProfileId && row.UpdatedAt <= observedAt)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(record => record.VolatileComponents, serialized)
                        .SetProperty(record => record.UpdatedAt, observedAt),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var record = await dbContext.LicensingSystemProfile
            .SingleOrDefaultAsync(row => row.Id == SingletonProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        if (record.UpdatedAt > observedAt)
        {
            return;
        }

        record.VolatileComponents = serialized;
        record.UpdatedAt = observedAt;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemProfileDrift>> ListDriftAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Replica clocks can disagree, so two records can carry the same instant. The identifier provides a
        // deterministic tie-breaker, but UUIDv7 does not establish the insertion order within that instant.
        var records = await dbContext.LicensingSystemProfileDrift
            .AsNoTracking()
            .OrderByDescending(row => row.OccurredAt)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToDrift).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task RecordHostnameAsync(
        string hostname,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        if (this.IsPostgres())
        {
            // One statement covers the first sighting and every later one, so replicas reporting the same host
            // name concurrently extend one row instead of colliding on the key.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO licensing_replica_hostnames (hostname, first_seen_at, last_seen_at)
                     VALUES ({hostname}, {observedAt}, {observedAt})
                     ON CONFLICT (hostname) DO UPDATE
                        SET last_seen_at = GREATEST(licensing_replica_hostnames.last_seen_at, EXCLUDED.last_seen_at)
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var record = await dbContext.LicensingReplicaHostnames
            .SingleOrDefaultAsync(row => row.Hostname == hostname, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            dbContext.LicensingReplicaHostnames.Add(
                new LicensingReplicaHostnameRecord
                {
                    Hostname = hostname,
                    FirstSeenAt = observedAt,
                    LastSeenAt = observedAt,
                });
        }
        else if (record.LastSeenAt < observedAt)
        {
            record.LastSeenAt = observedAt;
        }
        else
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReplicaHostname>> ListHostnamesAsync(CancellationToken cancellationToken = default)
    {
        var records = await dbContext.LicensingReplicaHostnames
            .AsNoTracking()
            .OrderByDescending(row => row.LastSeenAt)
            .ThenBy(row => row.Hostname)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records
            .Select(record => new ReplicaHostname(record.Hostname, record.FirstSeenAt, record.LastSeenAt))
            .ToList()
            .AsReadOnly();
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    private static SystemProfileSnapshot ToSnapshot(LicensingSystemProfileRecord record)
    {
        return new SystemProfileSnapshot
        {
            Stable = JsonSerializer.Deserialize<SystemProfileStableComponents>(
                         record.StableComponents,
                         SerializerOptions)
                     ?? new SystemProfileStableComponents(),
            Volatile = JsonSerializer.Deserialize<SystemProfileVolatileComponents>(
                           record.VolatileComponents,
                           SerializerOptions)
                       ?? new SystemProfileVolatileComponents(),
            ProfileHash = record.ProfileHash,
            CapturedAt = record.CapturedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }

    /// <summary>
    ///     Builds the row for one recorded change. The key is time-ordered, which gives the listing a stable
    ///     tie-breaker between records carrying the same instant. The bits below its timestamp are random, so
    ///     it does not put two records written within the same millisecond into the order they were written.
    /// </summary>
    private static LicensingSystemProfileDriftRecord ToRecord(SystemProfileDrift drift)
    {
        return new LicensingSystemProfileDriftRecord
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = drift.OccurredAt,
            ChangedComponents = JsonSerializer.Serialize(drift.ChangedComponents, SerializerOptions),
            PreviousHash = drift.PreviousHash,
            NewHash = drift.NewHash,
        };
    }

    private static SystemProfileDrift ToDrift(LicensingSystemProfileDriftRecord record)
    {
        return new SystemProfileDrift
        {
            OccurredAt = record.OccurredAt,
            ChangedComponents = JsonSerializer.Deserialize<List<string>>(record.ChangedComponents, SerializerOptions)
                                ?? [],
            PreviousHash = record.PreviousHash,
            NewHash = record.NewHash,
        };
    }
}
