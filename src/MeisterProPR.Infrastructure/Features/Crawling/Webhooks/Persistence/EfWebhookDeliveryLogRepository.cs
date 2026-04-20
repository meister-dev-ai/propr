// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;

/// <summary>Database-backed repository for durable webhook delivery-history entries.</summary>
public sealed class EfWebhookDeliveryLogRepository(MeisterProPRDbContext dbContext) : IWebhookDeliveryLogRepository
{
    /// <inheritdoc />
    public async Task<WebhookDeliveryLogEntryDto> AddAsync(
        Guid webhookConfigurationId,
        DateTimeOffset receivedAt,
        string eventType,
        WebhookDeliveryOutcome deliveryOutcome,
        int httpStatusCode,
        string? repositoryId,
        int? pullRequestId,
        string? sourceBranch,
        string? targetBranch,
        IReadOnlyList<string> actionSummaries,
        string? failureReason,
        CancellationToken ct = default)
    {
        await this.PurgeExpiredEntriesAsync(ct);

        var record = new WebhookDeliveryLogEntryRecord
        {
            Id = Guid.NewGuid(),
            WebhookConfigurationId = webhookConfigurationId,
            ReceivedAt = receivedAt,
            EventType = eventType,
            DeliveryOutcome = deliveryOutcome,
            HttpStatusCode = httpStatusCode,
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            ActionSummaries = actionSummaries.ToArray(),
            FailureReason = failureReason,
            FailureCategory = ProviderRetentionPolicy.CategorizeFailure(failureReason),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.WebhookDeliveryLogEntries.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookDeliveryLogEntryDto>> ListByWebhookConfigurationAsync(
        Guid webhookConfigurationId,
        int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "The take parameter must be greater than zero.");
        }

        await this.PurgeExpiredEntriesAsync(ct);

        var records = await dbContext.WebhookDeliveryLogEntries
            .Where(entry => entry.WebhookConfigurationId == webhookConfigurationId)
            .OrderByDescending(entry => entry.ReceivedAt)
            .Take(take)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    private static WebhookDeliveryLogEntryDto ToDto(WebhookDeliveryLogEntryRecord record)
    {
        return new WebhookDeliveryLogEntryDto(
            record.Id,
            record.WebhookConfigurationId,
            record.ReceivedAt,
            record.EventType,
            record.DeliveryOutcome,
            record.HttpStatusCode,
            record.RepositoryId,
            record.PullRequestId,
            record.SourceBranch,
            record.TargetBranch,
            record.ActionSummaries.ToList().AsReadOnly(),
            record.FailureReason,
            record.FailureCategory);
    }

    private async Task PurgeExpiredEntriesAsync(CancellationToken ct)
    {
        var cutoff = ProviderRetentionPolicy.GetWebhookDeliveryCutoff(DateTimeOffset.UtcNow);
        var expiredEntries = await dbContext.WebhookDeliveryLogEntries
            .Where(entry => entry.ReceivedAt < cutoff)
            .ToListAsync(ct);

        if (expiredEntries.Count == 0)
        {
            return;
        }

        dbContext.WebhookDeliveryLogEntries.RemoveRange(expiredEntries);
        await dbContext.SaveChangesAsync(ct);
    }
}
