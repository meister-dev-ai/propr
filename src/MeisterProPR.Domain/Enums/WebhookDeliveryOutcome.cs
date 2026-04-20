// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Final durable outcome recorded for a webhook delivery.</summary>
public enum WebhookDeliveryOutcome
{
    /// <summary>The delivery was accepted for downstream processing.</summary>
    Accepted = 0,

    /// <summary>The delivery was authenticated but intentionally ignored.</summary>
    Ignored = 1,

    /// <summary>The delivery was rejected before downstream processing.</summary>
    Rejected = 2,

    /// <summary>The delivery matched a configuration but failed during processing.</summary>
    Failed = 3,
}
