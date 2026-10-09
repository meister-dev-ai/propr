// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration.Persistence;

/// <summary>Shared PostgreSQL discovery observations and immutable generations with fenced refresh ownership.</summary>
/// <remarks>
/// Payload and page limits are 2 MiB; generation limits are 16 MiB and 128 generations per client.
/// The client budget accounts for 64 MiB including a 1 KiB allowance per record.
/// These limits do not measure physical database size or guarantee benchmarked storage efficiency.
/// </remarks>
public sealed class ClientPullRequestOverviewStore(IDbContextFactory<MeisterProPRDbContext> contexts, TimeProvider clock) : IClientPullRequestOverviewStore
{
    private const int MaximumClientBytes = 64 * 1024 * 1024;
    private const int MaximumSourceBytes = 2 * 1024 * 1024;

    private const int MaximumGenerationBytes = 16 * 1024 * 1024;

    // Reserve one bounded record for denial invalidation, including when content storage is full.
    private const int MaximumContentBytes = MaximumClientBytes - 1024;

    public async Task<string?> ReadInvalidationAsync(Guid clientId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClientPullRequestOverviewCache.AsNoTracking()
            .Where(item => item.ClientId == clientId && item.Key == "invalidation" && item.Kind == "invalidation")
            .Select(item => item.Fingerprint).SingleOrDefaultAsync(ct);
    }

    public async Task<OverviewSourceClaim> AcquireAsync(
        Guid clientId, string key, string fingerprint, DateTimeOffset now, CancellationToken ct, OverviewCacheScope? scope = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, clientId, ct);
        now = clock.GetUtcNow();
        await CleanupAsync(db, clientId, now, ct);
        var metadata = key.StartsWith("m:", StringComparison.Ordinal);
        if (scope is not null)
        {
            var denial = await db.ClientPullRequestOverviewCache.AsNoTracking().Where(item => item.ClientId == clientId &&
                                                                                              item.ExpiresAt > now && item.NextRefreshAt > now && (
                                                                                                  item.Failure == "authenticationDenied" &&
                                                                                                  item.ConnectionId == scope.ConnectionId ||
                                                                                                  item.Failure == "accessDenied" &&
                                                                                                  item.SourceKey == scope.SourceKey &&
                                                                                                  (metadata || item.Kind == "source")))
                .OrderByDescending(item => item.NextRefreshAt).FirstOrDefaultAsync(ct);
            if (denial is not null)
            {
                await transaction.CommitAsync(ct);
                return new(null, null, denial.Failure, null, denial.NextRefreshAt, null);
            }
        }

        var row = await db.ClientPullRequestOverviewCache.SingleOrDefaultAsync(item => item.ClientId == clientId && item.Key == key, ct);
        if (row is null)
        {
            // Bound coordination records as well as serialized content.
            var kind = metadata ? "metadata" : "source";
            if (kind == "source" && await db.ClientPullRequestOverviewCache.CountAsync(item => item.ClientId == clientId && item.Kind == "source", ct) >=
                12800 ||
                await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation")
                    .SumAsync(item => (long)item.ContentBytes + 1024, ct) + 1024 >
                MaximumContentBytes)
            {
                await transaction.CommitAsync(ct);
                return new(null, null, "capacity", null, now.AddSeconds(60), null);
            }

            row = new() { ClientId = clientId, Key = key, Kind = kind, Fingerprint = fingerprint, ExpiresAt = now.AddMinutes(15) };
            db.ClientPullRequestOverviewCache.Add(row);
        }

        if (scope is not null)
        {
            row.ConnectionId = scope.ConnectionId;
            row.SourceKey = scope.SourceKey;
        }

        var matches = row.Fingerprint == fingerprint;
        var retained = matches && row.ObservedAt > now.AddMinutes(-15);
        var content = retained ? row.Content : null;
        var observed = retained ? row.ObservedAt : null;
        var failure = matches ? row.Failure : "configurationChanged";
        Guid? owner = null;
        var expiredLease = row.Owner.HasValue && row.LeaseUntil <= now;
        if (expiredLease || !row.Owner.HasValue && row.NextRefreshAt <= now)
        {
            owner = row.Owner = Guid.NewGuid();
            row.LeaseUntil = now.AddSeconds(20);
            row.NextRefreshAt = now.AddSeconds(60);
            row.ExpiresAt = now.AddMinutes(15);
            if (!matches)
            {
                row.Content = null;
                row.ContentBytes = 0;
                row.ObservedAt = null;
            }

            row.Fingerprint = fingerprint;
            row.Failure = "pending";
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(owner, content, failure, observed, row.NextRefreshAt, row.LeaseUntil);
    }

    public async Task<bool> CompleteAsync(
        Guid clientId, string key, Guid owner, string fingerprint, string? content, string? failure, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, clientId, ct);
        now = clock.GetUtcNow();
        await CleanupAsync(db, clientId, now, ct);
        var row = await db.ClientPullRequestOverviewCache.SingleOrDefaultAsync(item => item.ClientId == clientId && item.Key == key, ct);
        if (row?.Owner != owner || row.Fingerprint != fingerprint || row.LeaseUntil <= now)
        {
            await transaction.CommitAsync(ct);
            return false;
        }

        var bytes = content is null ? 0 : Encoding.UTF8.GetByteCount(content);
        var used = await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation")
            .SumAsync(item => (long)item.ContentBytes + 1024, ct);
        if (failure is not ("accessDenied" or "authenticationDenied") && (bytes > MaximumSourceBytes || used - row.ContentBytes + bytes > MaximumContentBytes))
        {
            content = null;
            failure = "capacity";
        }

        if (failure is "accessDenied" or "authenticationDenied")
        {
            var connectionWide = failure == "authenticationDenied";
            var metadataOnly = row.Kind == "metadata";
            var nextRefreshAt = row.NextRefreshAt;
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId &&
                                                                  (item.Key == key ||
                                                                   connectionWide && row.ConnectionId != null && item.ConnectionId == row.ConnectionId ||
                                                                   !connectionWide && row.SourceKey != null && item.SourceKey == row.SourceKey &&
                                                                   (!metadataOnly || item.Kind == "metadata")))
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(item => item.Content, (string?)null)
                        .SetProperty(item => item.ContentBytes, 0)
                        .SetProperty(item => item.ObservedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.Failure, failure)
                        .SetProperty(item => item.Owner, (Guid?)null)
                        .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(item => item.NextRefreshAt, item => item.NextRefreshAt > nextRefreshAt ? item.NextRefreshAt : nextRefreshAt)
                        .SetProperty(item => item.ExpiresAt, now.AddMinutes(15)), ct);
            row.Content = null;
            row.ContentBytes = 0;
            row.ObservedAt = null;
            var invalidation = await db.ClientPullRequestOverviewCache.SingleOrDefaultAsync(
                item => item.ClientId == clientId && item.Key == "invalidation", ct);
            if (invalidation is null)
            {
                invalidation = new() { ClientId = clientId, Key = "invalidation", Kind = "invalidation" };
                db.ClientPullRequestOverviewCache.Add(invalidation);
            }

            invalidation.Fingerprint = Guid.NewGuid().ToString("N");
            // One permanent version record prevents cursor resurrection after recovery or lazy cleanup.
            invalidation.ExpiresAt = DateTimeOffset.MaxValue;
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId &&
                                                                  (item.Kind == "generation" || item.Kind == "page")).ExecuteDeleteAsync(ct);
        }
        else if (content is not null)
        {
            row.Content = content;
            row.ContentBytes = bytes;
            row.ObservedAt = now;
        }

        row.Failure = failure;
        row.Owner = null;
        row.LeaseUntil = null;
        row.ExpiresAt = now.AddMinutes(15);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SaveGenerationAsync(
        Guid clientId, Guid generation, string binding, string content, DateTimeOffset now, CancellationToken ct, DateTimeOffset? expiresAt = null)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, clientId, ct);
        now = clock.GetUtcNow();
        await CleanupAsync(db, clientId, now, ct);
        if (bytes > MaximumGenerationBytes || expiresAt <= now || expiresAt > now.AddMinutes(15) ||
            await db.ClientPullRequestOverviewCache.AnyAsync(item => item.ClientId == clientId && item.Key == generation.ToString("N"), ct) ||
            await db.ClientPullRequestOverviewCache.CountAsync(item => item.ClientId == clientId && item.Kind == "generation", ct) >= 128 ||
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation")
                .SumAsync(item => (long)item.ContentBytes + 1024, ct) + bytes +
            1024 > MaximumContentBytes)
        {
            await transaction.CommitAsync(ct);
            return false;
        }

        db.ClientPullRequestOverviewCache.Add(
            new()
            {
                ClientId = clientId, Key = generation.ToString("N"), Kind = "generation", Fingerprint = binding,
                Content = content, ContentBytes = bytes, ObservedAt = now, ExpiresAt = expiresAt ?? now.AddMinutes(15), NextRefreshAt = now.AddSeconds(60)
            });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<string?> ReadGenerationAsync(Guid clientId, Guid generation, string binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClientPullRequestOverviewCache.AsNoTracking()
            .Where(item => item.ClientId == clientId && item.Key == generation.ToString("N") && item.Kind == "generation" &&
                           item.Fingerprint == binding && item.ExpiresAt > now)
            .Select(item => item.Content).SingleOrDefaultAsync(ct);
    }

    public async Task<OverviewGeneration?> ReadGenerationStateAsync(Guid clientId, Guid generation, string binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        now = clock.GetUtcNow();
        var row = await db.ClientPullRequestOverviewCache.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ClientId == clientId && item.Key == generation.ToString("N") && item.Kind == "generation" &&
                        item.Fingerprint == binding && item.ExpiresAt > now, ct);
        return row?.Content is not null && row.ExpiresAt > clock.GetUtcNow() ? new(generation, row.Content, row.ExpiresAt) : null;
    }

    public async Task<OverviewGeneration?> GetOrCreateGenerationAsync(
        Guid clientId, string binding, string equivalence, string content, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, clientId, ct);
        now = clock.GetUtcNow();
        await CleanupAsync(db, clientId, now, ct);
        var existing = await db.ClientPullRequestOverviewCache.AsNoTracking().FirstOrDefaultAsync(
            item => item.ClientId == clientId &&
                    item.Kind == "generation" && item.Fingerprint == binding && item.SourceKey == equivalence && item.ExpiresAt > now, ct);
        if (existing?.Content is not null)
        {
            await transaction.CommitAsync(ct);
            return new(Guid.ParseExact(existing.Key, "N"), existing.Content, existing.ExpiresAt);
        }

        if (bytes > MaximumGenerationBytes || expiresAt <= now || expiresAt > now.AddMinutes(15) ||
            await db.ClientPullRequestOverviewCache.CountAsync(item => item.ClientId == clientId && item.Kind == "generation", ct) >= 128 ||
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation")
                .SumAsync(item => (long)item.ContentBytes + 1024, ct) + bytes + 1024 > MaximumContentBytes)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var id = Guid.NewGuid();
        db.ClientPullRequestOverviewCache.Add(
            new()
            {
                ClientId = clientId, Key = id.ToString("N"), Kind = "generation", Fingerprint = binding, SourceKey = equivalence,
                Content = content, ContentBytes = bytes, ObservedAt = now, ExpiresAt = expiresAt, NextRefreshAt = now.AddSeconds(60)
            });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(id, content, expiresAt);
    }

    public async Task<string?> ReadLatestGenerationAsync(Guid clientId, string binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<string>(
            $"""
             SELECT content AS "Value" FROM client_pull_request_overview_cache
             WHERE client_id = {clientId} AND kind = 'generation' AND fingerprint = {binding} AND expires_at > {now}
             ORDER BY observed_at DESC, (content::jsonb->>'attemptedSources')::integer DESC, key
             LIMIT 1
             """).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SavePageAsync(
        Guid clientId, string key, string binding, string content, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, clientId, ct);
        now = clock.GetUtcNow();
        await CleanupAsync(db, clientId, now, ct);
        var components = key.Split(':');
        var generation = components.Length == 4 && components[0] == "page" && Guid.TryParseExact(components[1], "N", out var parsed)
            ? await db.ClientPullRequestOverviewCache.SingleOrDefaultAsync(
                item => item.ClientId == clientId && item.Kind == "generation" &&
                        item.Key == parsed.ToString("N") && item.Fingerprint == binding && item.ExpiresAt > now, ct)
            : null;
        if (generation is null)
        {
            await transaction.CommitAsync(ct);
            return false;
        }

        expiresAt = expiresAt < generation.ExpiresAt ? expiresAt : generation.ExpiresAt;
        if (bytes > MaximumSourceBytes || expiresAt <= now ||
            await db.ClientPullRequestOverviewCache.AnyAsync(item => item.ClientId == clientId && item.Key == key, ct) ||
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation")
                .SumAsync(item => (long)item.ContentBytes + 1024, ct) + bytes +
            1024 > MaximumContentBytes)
        {
            await transaction.CommitAsync(ct);
            return false;
        }

        db.ClientPullRequestOverviewCache.Add(
            new()
            {
                ClientId = clientId, Key = key, Kind = "page", Fingerprint = binding, Content = content, ContentBytes = bytes,
                ObservedAt = now, ExpiresAt = expiresAt, NextRefreshAt = now.AddSeconds(60)
            });
        // A generation cannot recreate an immutable page after its metadata observation expires.
        generation.ExpiresAt = expiresAt;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<string?> ReadPageAsync(Guid clientId, string key, string binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClientPullRequestOverviewCache.AsNoTracking().Where(item => item.ClientId == clientId && item.Key == key &&
                                                                                    item.Kind == "page" && item.Fingerprint == binding && item.ExpiresAt > now)
            .Select(item => item.Content).SingleOrDefaultAsync(ct);
    }

    private static Task LockAsync(MeisterProPRDbContext db, Guid clientId, CancellationToken ct) =>
        PostgresAdvisoryLocks.AcquireTransactionAsync(db, 'p' + clientId.ToString("N"), ct);

    /// <summary>Performs bounded expiration cleanup during writes for one client.</summary>
    /// <remarks>
    /// Each call clears at most 256 expired source or metadata payloads and removes at most 256
    /// expired non-invalidation rows without active refresh leases. Durable invalidation rows remain.
    /// Clients without later writes can retain expired rows.
    /// </remarks>
    private static async Task CleanupAsync(MeisterProPRDbContext db, Guid clientId, DateTimeOffset now, CancellationToken ct)
    {
        var payloads = await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId &&
                                                                             (item.Kind == "source" || item.Kind == "metadata") && item.Content != null &&
                                                                             item.ObservedAt <= now.AddMinutes(-15))
            .OrderBy(item => item.ObservedAt).Take(256).Select(item => item.Key).ToArrayAsync(ct);
        if (payloads.Length > 0)
        {
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && payloads.Contains(item.Key))
                .ExecuteUpdateAsync(
                    update => update.SetProperty(item => item.Content, (string?)null)
                        .SetProperty(item => item.ContentBytes, 0).SetProperty(item => item.ObservedAt, (DateTimeOffset?)null), ct);
        }

        var keys = await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && item.Kind != "invalidation" && item.ExpiresAt <= now &&
                                                                         (item.Owner == null || item.LeaseUntil <= now)).OrderBy(item => item.ExpiresAt)
            .Take(256).Select(item => item.Key).ToArrayAsync(ct);
        if (keys.Length > 0)
        {
            await db.ClientPullRequestOverviewCache.Where(item => item.ClientId == clientId && keys.Contains(item.Key)).ExecuteDeleteAsync(ct);
        }
    }
}
