// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Where a verified webhook delivery has got to on its way to becoming a review.</summary>
public enum WebhookDeliveryQueueStatus
{
    /// <summary>Accepted and waiting. The provider has already been told the delivery was received.</summary>
    Pending = 0,

    /// <summary>Held by a replica that is turning it into a review.</summary>
    Processing = 1,

    /// <summary>Done. The delivery either queued a review or was deliberately skipped, and the log says which.</summary>
    Processed = 2,

    /// <summary>
    ///     Out of attempts. Kept rather than deleted, because a delivery that never became a review is
    ///     exactly what an operator needs to find, and the payload is what lets them replay it.
    /// </summary>
    Failed = 3,
}
