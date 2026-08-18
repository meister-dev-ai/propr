// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Persistence;

/// <summary>EF Core-backed store for the installation's usage-statistics identity and preference.</summary>
public sealed class UsageStatisticsStateRepository(MeisterProPRDbContext dbContext, TimeProvider timeProvider)
    : IUsageStatisticsStateStore
{
    private const int SingletonId = 1;
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    ///     How much advisory JSON is kept.
    ///     <para>
    ///         The response comes from a service the installation does not control, so its size is bounded
    ///         before it is stored.
    ///     </para>
    /// </summary>
    private const int MaxAdvisoriesJsonLength = 16 * 1024;

    /// <inheritdoc />
    public async Task<UsageStatisticsState> GetAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);
        return await this.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UsageStatisticsState> SetCommunityOptInAsync(
        bool optIn,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);

        var settings = await this.TrackSettingsAsync(cancellationToken);
        settings.CommunityOptIn = optIn;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedByUserId = actorUserId;

        await dbContext.SaveChangesAsync(cancellationToken);
        return await this.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UsageStatisticsState> RecordConsentGateSatisfiedAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);

        var settings = await this.TrackSettingsAsync(cancellationToken);
        if (settings.ConsentGateSatisfiedAt is null)
        {
            var now = timeProvider.GetUtcNow();
            settings.ConsentGateSatisfiedAt = now;
            settings.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await this.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UsageStatisticsState> RecordNoticeDismissedAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);

        var settings = await this.TrackSettingsAsync(cancellationToken);
        if (settings.NoticeDismissedAt is null)
        {
            var now = timeProvider.GetUtcNow();
            settings.NoticeDismissedAt = now;
            settings.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await this.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimSendAsync(
        DateTimeOffset notBefore,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);

        if (string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal))
        {
            var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE usage_statistics_settings
                    SET last_attempt_at = {claimedAt},
                        last_attempt_succeeded = NULL,
                        last_attempt_detail = NULL,
                        updated_at = {claimedAt}
                  WHERE id = {SingletonId}
                    AND (last_attempt_at IS NULL
                         OR last_attempt_at < {notBefore}
                         OR last_attempt_succeeded = FALSE)
                 """,
                cancellationToken);

            return claimed == 1;
        }

        var settings = await this.TrackSettingsAsync(cancellationToken);
        if (!IsClaimable(settings, notBefore))
        {
            return false;
        }

        settings.LastAttemptAt = claimedAt;
        settings.LastAttemptSucceeded = null;
        settings.LastAttemptDetail = null;
        settings.UpdatedAt = claimedAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    ///     Whether the day is available to claim.
    ///     <para>
    ///         Available when nothing has been attempted, when the interval has passed, or when the last
    ///         attempt failed. The last case is what stops a receiver outage from costing a day: a snapshot
    ///         that did not arrive cannot be a duplicate, so there is nothing to protect against.
    ///     </para>
    /// </summary>
    private static bool IsClaimable(UsageStatisticsSettingsRecord settings, DateTimeOffset notBefore)
    {
        if (settings.LastAttemptAt is not { } lastAttempt)
        {
            return true;
        }

        return lastAttempt < notBefore || settings.LastAttemptSucceeded == false;
    }

    /// <inheritdoc />
    public async Task<UsageStatisticsState> RecordSendOutcomeAsync(
        UsageStatisticsSendOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await this.EnsureSeededAsync(cancellationToken);

        var settings = await this.TrackSettingsAsync(cancellationToken);
        settings.LastAttemptAt = outcome.AttemptedAt;
        settings.LastAttemptSucceeded = outcome.Succeeded;
        settings.LastAttemptDetail = Truncate(outcome.Detail, 256);
        settings.UpdatedAt = outcome.AttemptedAt;

        if (outcome.Succeeded)
        {
            settings.LastSuccessAt = outcome.AttemptedAt;
        }

        // Update information is replaced only by a response that carried some, so a response with an empty
        // body leaves the previously reported version and advisories in place.
        if (outcome.Response is { } response && HasUpdateInformation(response))
        {
            settings.LatestVersion = Truncate(response.LatestVersion, 128);
            settings.AdvisoriesJson = SerializeAdvisories(response.Advisories);
            settings.UpdateInformationReceivedAt = outcome.AttemptedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await this.ReadAsync(cancellationToken);
    }

    private static bool HasUpdateInformation(UsageStatisticsPingResponse response)
    {
        return !string.IsNullOrWhiteSpace(response.LatestVersion) || response.Advisories.Count > 0;
    }

    /// <summary>
    ///     Serializes the advisories, keeping as many as fit within the limit.
    ///     <para>
    ///         Dropping all of them when the response is oversized would clear advisories already shown to an
    ///         operator. Entries are removed from the end instead, so the ones the receiver ordered first are
    ///         kept.
    ///     </para>
    /// </summary>
    private static string? SerializeAdvisories(IReadOnlyList<ProductAdvisory> advisories)
    {
        for (var count = advisories.Count; count > 0; count--)
        {
            var json = JsonSerializer.Serialize(
                advisories.Take(count),
                UsageStatisticsContract.SerializerOptions);

            if (json.Length <= MaxAdvisoriesJsonLength)
            {
                return json;
            }
        }

        return null;
    }

    private static IReadOnlyList<ProductAdvisory> DeserializeAdvisories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProductAdvisory>>(json, UsageStatisticsContract.SerializerOptions)
                   ?? [];
        }
        catch (JsonException)
        {
            // Display material only; no behaviour depends on it. Unreadable content reads as no advisories.
            return [];
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private async Task<UsageStatisticsSettingsRecord> TrackSettingsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.UsageStatisticsSettings
            .SingleAsync(record => record.Id == SingletonId, cancellationToken);
    }

    private async Task<UsageStatisticsState> ReadAsync(CancellationToken cancellationToken)
    {
        var identity = await dbContext.UsageStatisticsIdentity
            .AsNoTracking()
            .SingleAsync(record => record.Id == SingletonId, cancellationToken);
        var settings = await dbContext.UsageStatisticsSettings
            .AsNoTracking()
            .SingleAsync(record => record.Id == SingletonId, cancellationToken);

        return new UsageStatisticsState(
            identity.InstanceId,
            settings.CommunityOptIn,
            settings.ConsentGateSatisfiedAt,
            settings.NoticeDismissedAt,
            settings.LastAttemptAt,
            settings.LastAttemptSucceeded,
            settings.LastAttemptDetail,
            settings.LastSuccessAt,
            settings.LatestVersion,
            DeserializeAdvisories(settings.AdvisoriesJson),
            settings.UpdateInformationReceivedAt);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal))
        {
            // Two replicas starting together each propose an identifier. The conflict clause keeps the first
            // insert and the other replica reads that row, so an installation ends up with one identity.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO usage_statistics_identity (id, instance_id, created_at)
                 VALUES ({SingletonId}, {Guid.NewGuid()}, {now})
                 ON CONFLICT (id) DO NOTHING
                 """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO usage_statistics_settings (id, community_opt_in, updated_at)
                 VALUES ({SingletonId}, {true}, {now})
                 ON CONFLICT (id) DO NOTHING
                 """,
                cancellationToken);
            return;
        }

        await this.SeedWithoutUpsertAsync(now, cancellationToken);
    }

    /// <summary>Seeds the two singleton rows on providers without an upsert, which is the in-memory test host.</summary>
    private async Task SeedWithoutUpsertAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;

        if (!await dbContext.UsageStatisticsIdentity.AnyAsync(record => record.Id == SingletonId, cancellationToken))
        {
            dbContext.UsageStatisticsIdentity.Add(
                new UsageStatisticsIdentityRecord
                {
                    Id = SingletonId,
                    InstanceId = Guid.NewGuid(),
                    CreatedAt = now,
                });
            changed = true;
        }

        if (!await dbContext.UsageStatisticsSettings.AnyAsync(record => record.Id == SingletonId, cancellationToken))
        {
            dbContext.UsageStatisticsSettings.Add(
                new UsageStatisticsSettingsRecord
                {
                    Id = SingletonId,
                    CommunityOptIn = true,
                    UpdatedAt = now,
                });
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsAlreadySeededViolation(exception))
        {
            foreach (var entry in dbContext.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static bool IsAlreadySeededViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
