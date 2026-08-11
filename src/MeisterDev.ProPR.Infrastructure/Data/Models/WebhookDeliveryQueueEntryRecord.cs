// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for a verified webhook delivery waiting to be turned into a review.
///     <para>
///         The payload is kept whole rather than as the parsed pieces the intake needs. Re-parsing costs
///         nothing, and a delivery that a later build reads differently, because it uses a field the current
///         build ignores or avoids a bug the current build has, can be replayed as the provider sent it.
///     </para>
/// </summary>
public sealed class WebhookDeliveryQueueEntryRecord
{
    public Guid Id { get; set; }

    public Guid WebhookConfigurationId { get; set; }

    public WebhookConfigurationRecord? WebhookConfiguration { get; set; }

    public ScmProvider Provider { get; set; }

    public string PathKey { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>The provider's own identifier for this delivery, when it sends one.</summary>
    public string? DeliveryKey { get; set; }

    public string HeadersJson { get; set; } = "{}";

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    public WebhookDeliveryQueueStatus Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>When this entry becomes eligible again. Moved forward by the backoff after a failure.</summary>
    public DateTimeOffset EligibleAt { get; set; }

    /// <summary>Who is processing it, so a stalled replica's work can be recognised and taken back.</summary>
    public string? ClaimedBy { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }
}
