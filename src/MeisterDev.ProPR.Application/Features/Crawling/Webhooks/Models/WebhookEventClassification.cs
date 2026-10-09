// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Models;

/// <summary>Internal classification of a webhook event after secure intake validation.</summary>
public sealed record WebhookEventClassification(WebhookEventKind EventKind)
{
    /// <summary>Lower-case summary label used in delivery-history action summaries.</summary>
    public string SummaryLabel => this.EventKind switch
    {
        WebhookEventKind.PullRequestCreated => "pull request created",
        WebhookEventKind.PullRequestUpdated => "pull request updated",
        WebhookEventKind.PullRequestCommented => "pull request commented",
        WebhookEventKind.ReviewerAssigned => "reviewer assignment",
        WebhookEventKind.PullRequestClosed => "pull request closure",
        _ => "webhook event",
    };

    /// <summary>True when the classified event should synchronize review-job lifecycle rather than enqueue intake.</summary>
    public bool RequiresLifecycleSync => this.EventKind == WebhookEventKind.PullRequestClosed;
}

/// <summary>Downstream webhook event kinds recognized after payload normalization.</summary>
public enum WebhookEventKind
{
    /// <summary>The delivery indicates a new pull request.</summary>
    PullRequestCreated = 0,

    /// <summary>The delivery indicates a general pull-request update.</summary>
    PullRequestUpdated = 1,

    /// <summary>The delivery indicates a pull-request comment.</summary>
    PullRequestCommented = 2,

    /// <summary>The delivery indicates the configured reviewer is on the pull request.</summary>
    ReviewerAssigned = 3,

    /// <summary>The delivery indicates the pull request is no longer active.</summary>
    PullRequestClosed = 4,
}
