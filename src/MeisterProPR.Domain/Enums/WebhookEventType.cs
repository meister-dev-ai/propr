// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Webhook-triggered PR events supported by the system.</summary>
public enum WebhookEventType
{
    /// <summary>A pull request was created.</summary>
    PullRequestCreated = 0,

    /// <summary>A pull request was updated.</summary>
    PullRequestUpdated = 1,

    /// <summary>A pull request received a comment.</summary>
    PullRequestCommented = 2,
}
