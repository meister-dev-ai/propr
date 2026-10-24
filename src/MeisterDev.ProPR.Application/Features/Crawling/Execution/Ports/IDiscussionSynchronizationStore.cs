// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

public interface IDiscussionSynchronizationStore
{
    Task RequestFromDeliveryAsync(PullRequestThreadSnapshotTarget target, string? threadId, Guid deliveryId, CancellationToken ct = default);

    Task DeferAsync(Guid id, long requestedGeneration, TimeSpan delay, CancellationToken ct = default);
    Task<DiscussionSynchronizationState?> ReadAsync(PullRequestThreadSnapshotTarget target, CancellationToken ct = default);
    Task<bool> IsCurrentObservationAsync(PullRequestThreadSnapshotTarget target, DateTimeOffset observedAt, CancellationToken ct = default);
    Task RequestAsync(PullRequestThreadSnapshotTarget target, string? threadId = null, CancellationToken ct = default);

    Task<IDiscussionSynchronizationLease?> TryAcquireAsync(
        PullRequestThreadSnapshotTarget target, string? revision,
        bool force, TimeSpan reconciliationInterval, CancellationToken ct = default);

    Task<IDiscussionSynchronizationLease?> TryAcquireRetainedReplayAsync(
        PullRequestThreadSnapshotTarget target, TimeSpan leaseDuration,
        CancellationToken ct = default) => this.TryAcquireAsync(target, null, true, TimeSpan.FromMinutes(5), ct);

    Task<IReadOnlyList<DiscussionSynchronizationState>> ListDueAsync(TimeSpan reconciliationInterval, int limit, CancellationToken ct = default);
}

/// <summary>Serializes provider observation and all consumer writes for a scoped pull request.</summary>
public interface IDiscussionSynchronizationLease : IAsyncDisposable
{
    IDisposable BeginWrites() => DiscussionWriteFenceContext.Begin(null);
    DiscussionSynchronizationState State { get; }
    DateTimeOffset ObservedAt { get; }
    bool CompletionIsCurrent => false;
    Task EnsureCurrentAsync(CancellationToken ct = default);

    /// <summary>Persists successful consumer progress while retaining ownership and pending observation state.</summary>
    Task CheckpointProgressAsync(string progressJson, CancellationToken ct = default);

    Task CompleteAsync(
        string progressJson, string reviewerSummariesJson, DateTimeOffset observedAt, bool completeEnumeration,
        bool allConsumersCurrent, bool closed, string? revision, TimeSpan reconciliationInterval, CancellationToken ct = default);
}
