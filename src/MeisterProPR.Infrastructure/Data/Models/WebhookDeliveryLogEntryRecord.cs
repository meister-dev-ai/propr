// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a durable webhook delivery-history entry.</summary>
public sealed class WebhookDeliveryLogEntryRecord
{
    public Guid Id { get; set; }
    public Guid WebhookConfigurationId { get; set; }
    public WebhookConfigurationRecord? WebhookConfiguration { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public WebhookDeliveryOutcome DeliveryOutcome { get; set; }
    public int HttpStatusCode { get; set; }
    public string? RepositoryId { get; set; }
    public int? PullRequestId { get; set; }
    public string? SourceBranch { get; set; }
    public string? TargetBranch { get; set; }
    public string[] ActionSummaries { get; set; } = [];
    public string? FailureReason { get; set; }
    public string? FailureCategory { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
