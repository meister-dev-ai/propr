// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;

/// <summary>Coalesces durable refresh generations and holds a target lock through consumer writes.</summary>
public sealed class DiscussionSynchronizationStore(
    IDbContextFactory<MeisterProPRDbContext> factory,
    IScmProviderRegistry providers,
    TimeProvider? timeProvider = null,
    ILogger<DiscussionSynchronizationStore>? logger = null)
    : IDiscussionSynchronizationStore
{
    private readonly ILogger<DiscussionSynchronizationStore> _logger = logger ?? NullLogger<DiscussionSynchronizationStore>.Instance;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task DeferAsync(Guid id, long requestedGeneration, TimeSpan delay, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = await this.NowAsync(db, ct);
        await db.DiscussionSynchronizationStates.Where(state => state.Id == id && state.RequestedGeneration == requestedGeneration && state.LeaseToken == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(state => state.NextAttemptAt, now + delay)
                    .SetProperty(state => state.IsClosed, false).SetProperty(state => state.FullRefreshRequested, true), ct);
    }

    public async Task<DiscussionSynchronizationState?> ReadAsync(PullRequestThreadSnapshotTarget target, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = DiscussionSynchronizationState.Identity(
            target.ClientId, target.ConnectionId, target.Provider,
            target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId, target.PullRequestId);
        return await db.DiscussionSynchronizationStates.AsNoTracking().SingleOrDefaultAsync(state => state.Id == id, ct);
    }

    public async Task<bool> IsCurrentObservationAsync(PullRequestThreadSnapshotTarget target, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = DiscussionSynchronizationState.Identity(
            target.ClientId, target.ConnectionId, target.Provider,
            target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId, target.PullRequestId);
        var sourceId = target.GetEvidenceSourceId(providers);
        return await db.DiscussionObservationFences.AnyAsync(
            fence =>
                fence.Id == sourceId && fence.OwnerStateId == id && fence.ObservedAt == observedAt && fence.Completed, ct);
    }

    public async Task RequestAsync(PullRequestThreadSnapshotTarget target, string? threadId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequestInTransactionAsync(db, target, threadId, await this.NowAsync(db, ct), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RequestFromDeliveryAsync(PullRequestThreadSnapshotTarget target, string? threadId, Guid deliveryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var delivery = await db.WebhookDeliveryQueue.FromSqlInterpolated($"SELECT * FROM webhook_delivery_queue WHERE id={deliveryId} FOR UPDATE")
            .SingleAsync(ct);
        if (delivery.Provider != target.Provider || !await db.WebhookConfigurations.AnyAsync(
                row => row.Id == delivery.WebhookConfigurationId && row.ClientId == target.ClientId, ct))
        {
            throw new InvalidOperationException("The queued delivery does not belong to the discussion target.");
        }

        if (!delivery.DiscussionRefreshRequested)
        {
            await RequestInTransactionAsync(db, target, threadId, await this.NowAsync(db, ct), ct);
            delivery.DiscussionRefreshRequested = true;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Locks the target row before coalescing concurrent refresh requests.</summary>
    internal static async Task RequestInTransactionAsync(
        MeisterProPRDbContext db, PullRequestThreadSnapshotTarget target,
        string? threadId, DateTimeOffset now, CancellationToken ct)
    {
        var id = await EnsureAsync(db, target, ct);
        var state = await db.DiscussionSynchronizationStates.FromSqlInterpolated($"SELECT * FROM discussion_synchronization_states WHERE id={id} FOR UPDATE")
            .SingleAsync(ct);
        await db.Entry(state).ReloadAsync(ct);
        state.RequestedGeneration = checked(state.RequestedGeneration + 1);
        state.IsClosed = false;
        state.NextAttemptAt = now;
        var pending = JsonSerializer.Deserialize<HashSet<string>>(state.PendingThreadIdsJson) ?? [];
        if (threadId is null || pending.Count >= 64)
        {
            state.FullRefreshRequested = true;
            pending.Clear();
        }
        else if (!state.FullRefreshRequested)
        {
            pending.Add(threadId);
        }

        state.PendingThreadIdsJson = JsonSerializer.Serialize(pending);
        await db.SaveChangesAsync(ct);
    }

    public Task<IDiscussionSynchronizationLease?> TryAcquireAsync(
        PullRequestThreadSnapshotTarget target, string? revision,
        bool force, TimeSpan reconciliationInterval, CancellationToken ct = default) =>
        this.TryAcquireCoreAsync(target, revision, force, reconciliationInterval, LeaseDuration, ct);

    public Task<IDiscussionSynchronizationLease?> TryAcquireRetainedReplayAsync(
        PullRequestThreadSnapshotTarget target, TimeSpan leaseDuration,
        CancellationToken ct = default) => this.TryAcquireCoreAsync(
        target, null, true, TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(Math.Clamp(leaseDuration.TotalMinutes, 1, 60)), ct);

    private async Task<IDiscussionSynchronizationLease?> TryAcquireCoreAsync(
        PullRequestThreadSnapshotTarget target, string? revision,
        bool force, TimeSpan reconciliationInterval, TimeSpan leaseDuration, CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        IAsyncDisposable? sourceLock = null;
        var id = DiscussionSynchronizationState.Identity(
            target.ClientId, target.ConnectionId, target.Provider,
            target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId, target.PullRequestId);
        var fenceId = target.GetEvidenceSourceId(providers);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            sourceLock = await PostgresAdvisoryLocks.TryAcquireSessionAsync(
                (NpgsqlConnection)db.Database.GetDbConnection(), $"discussion-source:{fenceId:D}", ct);
            if (sourceLock is null)
            {
                await db.DisposeAsync();
                return null;
            }

            await EnsureAsync(db, target, ct);
            var state = await db.DiscussionSynchronizationStates.SingleAsync(state => state.Id == id, ct);
            var now = await this.NowAsync(db, ct);
            var pending = state.RequestedGeneration > state.CompletedGeneration;
            var due = state.FullRefreshRequested || state.LastFullReconciledAt is null || state.LastFullReconciledAt <= now - reconciliationInterval;
            var revisionMoved = revision is not null && revision != state.LastReviewRevision;
            if (!force && !revisionMoved && (!pending && !due || state.NextAttemptAt > now))
            {
                await sourceLock.DisposeAsync();
                await db.DisposeAsync();
                return null;
            }

            var fence = await db.DiscussionObservationFences.SingleOrDefaultAsync(row => row.Id == fenceId, ct);
            var token = Guid.NewGuid();
            if (fence is not null)
            {
                var interrupted = !fence.Completed || fence.OwnerStateId != state.Id;
                var unfinished = state.ReadProgress();
                var checkpoint = fence.OwnerStateId == state.Id
                    ? MemoryCheckpoint(fence.Token, state.CollectionGeneration, state.RetentionGeneration)
                    : null;
                var nextCheckpoint = MemoryCheckpoint(token, state.CollectionGeneration, state.RetentionGeneration);
                foreach (var progress in unfinished.Values)
                {
                    var memory = checkpoint is not null && progress.MemoryCheckpoint == checkpoint
                        ? progress.Consumers.GetValueOrDefault("memory")
                        : null;
                    if (interrupted)
                    {
                        progress.Consumers.Clear();
                        if (memory is not null)
                        {
                            progress.Consumers["memory"] = memory;
                        }
                    }

                    progress.MemoryCheckpoint = memory is not null ? nextCheckpoint : null;
                }

                state.ThreadProgressJson = JsonSerializer.Serialize(unfinished);
            }

            state.LeaseToken = token;
            state.LeaseExpiresAt = now + leaseDuration;
            state.ObservationSequence = checked(state.ObservationSequence + 1);
            var observationTime = fence is null || fence.ObservedAt < now ? now : fence.ObservedAt.AddTicks(10);
            if (fence is null)
            {
                fence = new DiscussionObservationFenceState { Id = fenceId };
                db.DiscussionObservationFences.Add(fence);
            }

            fence.OwnerStateId = state.Id;
            fence.Token = state.LeaseToken.Value;
            fence.ExpiresAt = state.LeaseExpiresAt.Value;
            fence.ObservedAt = observationTime;
            fence.Completed = false;
            await db.SaveChangesAsync(ct);
            return new Lease(db, sourceLock, state, fenceId, observationTime, this.NowAsync, this._logger, leaseDuration);
        }
        catch
        {
            if (sourceLock is not null)
            {
                await DisposeSafelyAsync(sourceLock, this._logger, "source lock");
            }

            await DisposeSafelyAsync(db, this._logger, "database context");
            throw;
        }
    }

    private static string MemoryCheckpoint(Guid token, long collectionGeneration, long retentionGeneration) =>
        DiscussionThreadProgress.Fingerprint(
            token.ToString("D"), collectionGeneration.ToString(CultureInfo.InvariantCulture), retentionGeneration.ToString(CultureInfo.InvariantCulture));

    /// <summary>Uses PostgreSQL window ranking to preserve client fairness before the bounded batch limit.</summary>
    public async Task<IReadOnlyList<DiscussionSynchronizationState>> ListDueAsync(TimeSpan reconciliationInterval, int limit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = await this.NowAsync(db, ct);
        var cutoff = now - reconciliationInterval;
        return await db.DiscussionSynchronizationStates.FromSqlInterpolated(
            $"""
             SELECT state.* FROM discussion_synchronization_states AS state
             JOIN (
                 SELECT id, ROW_NUMBER() OVER (
                     PARTITION BY client_id,provider,lower(split_part(provider_scope_path,'/',3))
                     ORDER BY next_attempt_at,id) AS position
                 FROM discussion_synchronization_states
                 WHERE NOT is_closed AND next_attempt_at <= {now}
                   AND (full_refresh_requested OR requested_generation > completed_generation
                        OR last_full_reconciled_at IS NULL OR last_full_reconciled_at <= {cutoff})
             ) AS due ON due.id=state.id
             ORDER BY due.position,state.next_attempt_at,state.client_id,state.id
             LIMIT {limit}
             """).AsNoTracking().ToListAsync(ct);
    }

    private Task<DateTimeOffset> NowAsync(MeisterProPRDbContext db, CancellationToken ct)
    {
        return timeProvider is not null && timeProvider != TimeProvider.System
            ? Task.FromResult(this._clock.GetUtcNow())
            : db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    }

    /// <summary>Uses PostgreSQL conflict handling to create one target without aborting a concurrent caller's transaction.</summary>
    private static async Task<Guid> EnsureAsync(MeisterProPRDbContext db, PullRequestThreadSnapshotTarget target, CancellationToken ct)
    {
        var id = DiscussionSynchronizationState.Identity(
            target.ClientId, target.ConnectionId, target.Provider,
            target.ProviderScopePath, target.ProviderProjectKey, target.RepositoryId, target.PullRequestId);
        var emptyProgress = "{}";
        if (!await db.DiscussionSynchronizationStates.AnyAsync(state => state.Id == id, ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO discussion_synchronization_states
                 (id,client_id,connection_id,provider,provider_scope_path,provider_project_key,repository_id,pull_request_id,
                  requested_generation,completed_generation,observation_sequence,next_attempt_at,is_closed,full_refresh_requested,retention_generation,collection_generation,
                  pending_thread_ids_json,thread_progress_json,reviewer_summaries_json)
                 VALUES ({id},{target.ClientId},{target.ConnectionId},{(int)target.Provider},{target.ProviderScopePath},{target.ProviderProjectKey},
                  {target.RepositoryId},{target.PullRequestId},1,0,0,CURRENT_TIMESTAMP,false,true,0,0,'[]'::jsonb,{emptyProgress}::jsonb,'[]'::jsonb)
                 ON CONFLICT (id) DO NOTHING
                 """, ct);
        }

        return id;
    }

    private static async ValueTask DisposeSafelyAsync(IAsyncDisposable resource, ILogger logger, string name)
    {
        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning("Discussion {Resource} cleanup failed: {ExceptionType}, code {ErrorCode}.", name, exception.GetType().Name, exception.HResult);
        }
    }

    private sealed class Lease(
        MeisterProPRDbContext db,
        IAsyncDisposable sourceLock,
        DiscussionSynchronizationState state,
        Guid fenceId,
        DateTimeOffset observationTime,
        Func<MeisterProPRDbContext, CancellationToken, Task<DateTimeOffset>> nowAsync,
        ILogger logger,
        TimeSpan leaseDuration)
        : IDiscussionSynchronizationLease
    {
        private readonly Guid _token = state.LeaseToken!.Value;
        private readonly long _generation = state.RequestedGeneration;
        private readonly long _sequence = state.ObservationSequence;
        private readonly long _collectionGeneration = state.CollectionGeneration;
        private readonly long _retentionGeneration = state.RetentionGeneration;
        private readonly SemaphoreSlim _operations = new(1, 1);
        private bool _completed;
        private bool _disposed;
        public DiscussionSynchronizationState State => state;
        public DateTimeOffset ObservedAt { get; } = observationTime;
        public bool CompletionIsCurrent { get; private set; }
        public IDisposable BeginWrites() => DiscussionWriteFenceContext.Begin(new(fenceId, this._token));

        public async Task EnsureCurrentAsync(CancellationToken ct = default)
        {
            await this._operations.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                await this.EnsureCurrentCoreAsync(ct);
            }
            finally
            {
                this._operations.Release();
            }
        }

        private async Task EnsureCurrentCoreAsync(CancellationToken ct)
        {
            await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var now = await nowAsync(db, ct);
            var renewed = now + leaseDuration;
            if (this._completed || state.LeaseExpiresAt <= now || await db.DiscussionSynchronizationStates.Where(row => row.Id == state.Id &&
                        row.LeaseToken == this._token && row.ObservationSequence == this._sequence && row.LeaseExpiresAt > now)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAt, renewed), ct) != 1)
            {
                throw new InvalidOperationException("The discussion synchronization lease expired or was replaced.");
            }

            if (await db.DiscussionObservationFences.Where(row => row.Id == fenceId && row.OwnerStateId == state.Id &&
                                                                  row.Token == this._token && row.ExpiresAt > now)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, renewed), ct) != 1)
            {
                throw new InvalidOperationException("The shared discussion observation was superseded.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            state.LeaseExpiresAt = renewed;
        }

        public async Task CheckpointProgressAsync(string progressJson, CancellationToken ct = default)
        {
            await this._operations.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var fence = await db.DiscussionObservationFences
                    .FromSqlInterpolated($"SELECT * FROM discussion_observation_fences WHERE id={fenceId} AND token={this._token} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                var now = await nowAsync(db, ct);
                if (this._completed || state.LeaseExpiresAt <= now || fence is null || fence.OwnerStateId != state.Id || fence.ExpiresAt <= now ||
                    fence.Completed)
                {
                    throw new InvalidOperationException("The discussion observation is no longer current.");
                }

                var progress = JsonSerializer.Deserialize<Dictionary<string, DiscussionThreadProgress>>(progressJson) ?? [];
                var checkpoint = MemoryCheckpoint(this._token, this._collectionGeneration, this._retentionGeneration);
                foreach (var thread in progress.Values)
                {
                    thread.MemoryCheckpoint = thread.Consumers.ContainsKey("memory") ? checkpoint : null;
                }

                var checkpointJson = JsonSerializer.Serialize(progress);
                var changed = await db.DiscussionSynchronizationStates.Where(row => row.Id == state.Id && row.LeaseToken == this._token &&
                                                                                    row.ObservationSequence == this._sequence && row.LeaseExpiresAt > now &&
                                                                                    row.CollectionGeneration == this._collectionGeneration &&
                                                                                    row.RetentionGeneration == this._retentionGeneration)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ThreadProgressJson, checkpointJson), ct);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The discussion observation or its consumer policies changed.");
                }

                await transaction.CommitAsync(ct);
                state.ThreadProgressJson = checkpointJson;
            }
            finally
            {
                this._operations.Release();
            }
        }

        public async Task CompleteAsync(
            string progressJson, string reviewerSummariesJson, DateTimeOffset observedAt, bool completeEnumeration,
            bool allConsumersCurrent, bool closed, string? revision, TimeSpan reconciliationInterval, CancellationToken ct = default)
        {
            await this._operations.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var currentFence = await db.DiscussionObservationFences
                    .FromSqlInterpolated($"SELECT * FROM discussion_observation_fences WHERE id={fenceId} AND token={this._token} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                if (currentFence is null)
                {
                    throw new InvalidOperationException("The shared discussion observation was superseded.");
                }

                await this.EnsureCurrentCoreAsync(ct);
                var progress = JsonSerializer.Deserialize<Dictionary<string, DiscussionThreadProgress>>(progressJson) ?? [];
                var checkpointed = state.ReadProgress();
                var checkpoint = MemoryCheckpoint(this._token, this._collectionGeneration, this._retentionGeneration);
                foreach (var (threadId, thread) in progress)
                {
                    thread.MemoryCheckpoint = checkpointed.TryGetValue(threadId, out var previous) && previous.MemoryCheckpoint == checkpoint &&
                                              thread.Consumers.TryGetValue("memory", out var acknowledgment) &&
                                              previous.Consumers.GetValueOrDefault("memory") == acknowledgment
                        ? checkpoint
                        : null;
                }

                progressJson = JsonSerializer.Serialize(progress);
                var now = await nowAsync(db, ct);
                var fullObservedAt = completeEnumeration ? observedAt : state.LastFullReconciledAt;
                var changed = await db.DiscussionSynchronizationStates.Where(row => row.Id == state.Id && row.LeaseToken == this._token &&
                                                                                    row.ObservationSequence == this._sequence && row.LeaseExpiresAt > now &&
                                                                                    (row.LastObservedAt == null || row.LastObservedAt <= observedAt))
                    .ExecuteUpdateAsync(
                        set => set.SetProperty(row => row.ThreadProgressJson, progressJson)
                            .SetProperty(row => row.ReviewerSummariesJson, reviewerSummariesJson)
                            .SetProperty(row => row.LastObservedAt, observedAt).SetProperty(row => row.LastFullReconciledAt, fullObservedAt)
                            .SetProperty(row => row.LastReviewRevision, revision).SetProperty(
                                row => row.IsClosed, row => closed && allConsumersCurrent &&
                                                            row.RequestedGeneration <= this._generation &&
                                                            row.RetentionGeneration == state.RetentionGeneration &&
                                                            row.CollectionGeneration == state.CollectionGeneration)
                            .SetProperty(row => row.CompletedGeneration, row => allConsumersCurrent ? this._generation : row.CompletedGeneration)
                            .SetProperty(
                                row => row.NextAttemptAt, row =>
                                    row.RequestedGeneration > this._generation || row.RetentionGeneration != state.RetentionGeneration ||
                                    row.CollectionGeneration != state.CollectionGeneration
                                        ? now
                                        : allConsumersCurrent
                                            ? (completeEnumeration ? now + reconciliationInterval : (fullObservedAt ?? now) + reconciliationInterval)
                                            : now + TimeSpan.FromSeconds(15))
                            .SetProperty(
                                row => row.FullRefreshRequested,
                                row => row.RequestedGeneration > this._generation || row.RetentionGeneration != state.RetentionGeneration ||
                                       row.CollectionGeneration != state.CollectionGeneration || !allConsumersCurrent)
                            .SetProperty(row => row.PendingThreadIdsJson, row => row.RequestedGeneration > this._generation ? row.PendingThreadIdsJson : "[]")
                            .SetProperty(row => row.LeaseToken, (Guid?)null).SetProperty(row => row.LeaseExpiresAt, (DateTimeOffset?)null), ct);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The discussion observation was superseded.");
                }

                await db.DiscussionObservationFences.Where(row => row.Id == fenceId && row.Token == this._token)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.Completed, true), ct);
                var capturedStillCurrent = await db.DiscussionSynchronizationStates.Where(row => row.Id == state.Id)
                    .Select(row => row.RequestedGeneration == this._generation && row.RetentionGeneration == state.RetentionGeneration &&
                                   row.CollectionGeneration == state.CollectionGeneration).SingleAsync(ct);
                await transaction.CommitAsync(ct);
                this.CompletionIsCurrent = capturedStillCurrent;
                this._completed = true;
            }
            finally
            {
                this._operations.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this._operations.WaitAsync();
            try
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                try
                {
                    if (!this._completed)
                    {
                        var now = await nowAsync(db, CancellationToken.None);
                        var retryAt = now + TimeSpan.FromSeconds(15);
                        await db.DiscussionSynchronizationStates.Where(row => row.Id == state.Id && row.LeaseToken == this._token)
                            .ExecuteUpdateAsync(
                                set => set.SetProperty(row => row.LeaseToken, (Guid?)null)
                                    .SetProperty(row => row.LeaseExpiresAt, (DateTimeOffset?)null)
                                    .SetProperty(row => row.FullRefreshRequested, true)
                                    .SetProperty(row => row.NextAttemptAt, row => row.RequestedGeneration > this._generation ? now : retryAt),
                                CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Discussion lease cleanup failed: {ExceptionType}, code {ErrorCode}.", exception.GetType().Name, exception.HResult);
                }

                await DisposeSafelyAsync(sourceLock, logger, "source lock");
                await DisposeSafelyAsync(db, logger, "database context");
            }
            finally
            {
                this._operations.Release();
            }
        }
    }
}
