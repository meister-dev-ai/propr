// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     How the worker that turns accepted webhook deliveries into reviews paces itself.
/// </summary>
public sealed class WebhookDeliveryWorkerOptions
{
    /// <summary>
    ///     Wait between polls when the queue is empty. Bound to
    ///     <c>WEBHOOK_DELIVERY_IDLE_POLL_SECONDS</c>. A backlog is drained without waiting at all; this is
    ///     only how often an idle installation asks.
    /// </summary>
    [Range(1, 300)]
    public int IdlePollIntervalSeconds { get; set; } = 2;

    /// <summary>
    ///     How many deliveries this replica works at once. Bound to
    ///     <c>WEBHOOK_DELIVERY_MAX_CONCURRENCY</c>.
    ///     <para>
    ///         One at a time was the original choice, on the reasoning that the reviews a delivery queues
    ///         have their own concurrency and draining faster would only move contention onto the provider's
    ///         rate limit. Measured against a runner fleet, that reasoning does not hold: intake supplied
    ///         roughly one job every four seconds while six execution slots stood idle, so the fleet was
    ///         limited by how fast work was created rather than by how fast it could be done.
    ///     </para>
    ///     <para>
    ///         Raise it to fill a fleet; the ceiling worth respecting is the provider's rate limit, since
    ///         each delivery reads a pull request from the provider that sent it.
    ///     </para>
    /// </summary>
    [Range(1, 32)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    ///     How long a claim is good for. Bound to <c>WEBHOOK_DELIVERY_CLAIM_SECONDS</c>. A replica that
    ///     dies holding one has its delivery returned after this, so it must comfortably exceed the
    ///     slowest realistic intake rather than the average one.
    /// </summary>
    [Range(30, 3600)]
    public int ClaimDurationSeconds { get; set; } = 300;

    /// <summary>
    ///     How many times a delivery is tried before it is kept as failed. Bound to
    ///     <c>WEBHOOK_DELIVERY_MAX_ATTEMPTS</c>.
    /// </summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    ///     Wait before a failed delivery is eligible again. Bound to
    ///     <c>WEBHOOK_DELIVERY_RETRY_BACKOFF_SECONDS</c>.
    /// </summary>
    [Range(1, 3600)]
    public int RetryBackoffSeconds { get; set; } = 30;

    /// <summary>The idle poll interval as a <see cref="TimeSpan" />.</summary>
    public TimeSpan IdlePollInterval => TimeSpan.FromSeconds(this.IdlePollIntervalSeconds);

    /// <summary>The claim duration as a <see cref="TimeSpan" />.</summary>
    public TimeSpan ClaimDuration => TimeSpan.FromSeconds(this.ClaimDurationSeconds);

    /// <summary>The retry backoff as a <see cref="TimeSpan" />.</summary>
    public TimeSpan RetryBackoff => TimeSpan.FromSeconds(this.RetryBackoffSeconds);
}
