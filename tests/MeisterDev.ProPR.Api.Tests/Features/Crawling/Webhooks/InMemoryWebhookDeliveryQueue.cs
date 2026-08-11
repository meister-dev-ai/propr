// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling.Webhooks;

/// <summary>
///     Holds accepted deliveries for tests that drive the whole path, meaning receiver, queue and worker,
///     against an in-memory database. The real queue is one raw statement with <c>FOR UPDATE SKIP LOCKED</c>, which only
///     PostgreSQL can answer; what it does under two replicas is covered there, and what these tests need is
///     somewhere for a delivery to wait between being answered and being worked.
/// </summary>
public sealed class InMemoryWebhookDeliveryQueue : IWebhookDeliveryQueue
{
    private readonly ConcurrentQueue<WebhookDeliveryQueueItem> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> EnqueueAsync(WebhookDeliveryQueueSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (!string.IsNullOrWhiteSpace(submission.DeliveryKey)
            && !this._keys.TryAdd($"{submission.WebhookConfigurationId}/{submission.DeliveryKey}", 0))
        {
            return Task.FromResult(false);
        }

        this._pending.Enqueue(
            new WebhookDeliveryQueueItem(
                Guid.NewGuid(),
                submission.Provider,
                submission.PathKey,
                submission.HeadersJson,
                submission.Payload,
                1));

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<WebhookDeliveryQueueItem?> ClaimNextAsync(
        string owner,
        TimeSpan claimDuration,
        CancellationToken ct = default)
    {
        return Task.FromResult(this._pending.TryDequeue(out var item) ? item : null);
    }

    /// <inheritdoc />
    public Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FailAsync(
        Guid id,
        string error,
        int maxAttempts,
        TimeSpan backoff,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReleaseExpiredClaimsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(0);
    }

    /// <summary>Empties the queue between scenarios, so one test's delivery is not another's backlog.</summary>
    public void Clear()
    {
        this._pending.Clear();
        this._keys.Clear();
    }
}
