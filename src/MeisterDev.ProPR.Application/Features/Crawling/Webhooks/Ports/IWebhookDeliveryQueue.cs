// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>
///     Where a verified delivery waits between being accepted and becoming a review.
///     <para>
///         Intake used to run inside the provider's HTTP request. Reading a pull request takes seconds, a
///         burst of deliveries queues behind itself, and every provider times out after a few seconds, so the
///         deliveries that arrived together were the ones dropped, and nothing recorded the loss. Accepting
///         first and working afterwards separates an external timeout from whether a review happens.
///     </para>
/// </summary>
public interface IWebhookDeliveryQueue
{
    /// <summary>
    ///     Accepts a verified delivery. Returns false when this delivery is already queued, which is how a
    ///     provider's own retry is recognised rather than reviewed twice.
    /// </summary>
    Task<bool> EnqueueAsync(WebhookDeliveryQueueSubmission submission, CancellationToken ct = default);

    /// <summary>
    ///     Takes the oldest eligible delivery, or null when there is none. Claiming is atomic: two replicas
    ///     polling together cannot both get the same one.
    /// </summary>
    Task<WebhookDeliveryQueueItem?> ClaimNextAsync(string owner, TimeSpan claimDuration, CancellationToken ct = default);

    /// <summary>Marks a claimed delivery done, however it turned out.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Hands a claimed delivery back after a failure, to be retried once the backoff has passed, or
    ///     gives up on it when it has had enough attempts.
    /// </summary>
    Task FailAsync(Guid id, string error, int maxAttempts, TimeSpan backoff, CancellationToken ct = default);

    /// <summary>Returns claims whose holder stopped reporting, so the work is not stranded.</summary>
    Task<int> ReleaseExpiredClaimsAsync(CancellationToken ct = default);
}

/// <summary>A verified delivery being handed to the queue.</summary>
/// <param name="WebhookConfigurationId">The configuration it arrived on.</param>
/// <param name="Provider">The provider family.</param>
/// <param name="PathKey">The configuration's path key, so the worker can resolve it again.</param>
/// <param name="EventType">The event, for the operator-facing list.</param>
/// <param name="DeliveryKey">The provider's own delivery identifier, when it sends one.</param>
/// <param name="HeadersJson">The delivery's headers, serialised.</param>
/// <param name="Payload">The delivery body, exactly as it arrived.</param>
public sealed record WebhookDeliveryQueueSubmission(
    Guid WebhookConfigurationId,
    ScmProvider Provider,
    string PathKey,
    string EventType,
    string? DeliveryKey,
    string HeadersJson,
    string Payload);

/// <summary>A delivery claimed for processing.</summary>
/// <param name="Id">The queue entry.</param>
/// <param name="Provider">The provider family.</param>
/// <param name="PathKey">The configuration's path key.</param>
/// <param name="HeadersJson">The delivery's headers, serialised.</param>
/// <param name="Payload">The delivery body, exactly as it arrived.</param>
/// <param name="Attempts">How many times it has been tried, including this one.</param>
public sealed record WebhookDeliveryQueueItem(
    Guid Id,
    ScmProvider Provider,
    string PathKey,
    string HeadersJson,
    string Payload,
    int Attempts);
