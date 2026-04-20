// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class EfWebhookDeliveryLogRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_WebhookDeliveryLogs_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static async Task<Guid> SeedWebhookConfigurationAsync(MeisterProPRDbContext db)
    {
        var clientId = Guid.NewGuid();
        var configurationId = Guid.NewGuid();

        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                DisplayName = "Webhook Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        db.WebhookConfigurations.Add(
            new WebhookConfigurationRecord
            {
                Id = configurationId,
                ClientId = clientId,
                ProviderType = WebhookProviderType.GitHub,
                PublicPathKey = "provider-audit-test",
                OrganizationUrl = "https://github.com/acme",
                ProjectId = "acme/platform",
                SecretCiphertext = "ciphertext",
                IsActive = true,
                EnabledEvents = [WebhookEventType.PullRequestUpdated.ToString()],
                CreatedAt = DateTimeOffset.UtcNow,
            });

        await db.SaveChangesAsync();
        return configurationId;
    }

    [Fact]
    public async Task AddAsync_FailedWebhookDelivery_PersistsFailureCategory_AndPurgesExpiredEntries()
    {
        await using var db = CreateContext();
        var configurationId = await SeedWebhookConfigurationAsync(db);
        var cutoff = ProviderRetentionPolicy.GetWebhookDeliveryCutoff(DateTimeOffset.UtcNow);

        db.WebhookDeliveryLogEntries.Add(
            new WebhookDeliveryLogEntryRecord
            {
                Id = Guid.NewGuid(),
                WebhookConfigurationId = configurationId,
                ReceivedAt = cutoff.AddMinutes(-1),
                EventType = "obsolete",
                DeliveryOutcome = WebhookDeliveryOutcome.Rejected,
                HttpStatusCode = 401,
                ActionSummaries = [],
                FailureReason = "Expired delivery log entry.",
                FailureCategory = "unknown",
                CreatedAt = cutoff.AddMinutes(-1),
            });
        await db.SaveChangesAsync();

        var sut = new EfWebhookDeliveryLogRepository(db);

        var created = await sut.AddAsync(
            configurationId,
            DateTimeOffset.UtcNow,
            "Merge Request Hook",
            WebhookDeliveryOutcome.Rejected,
            401,
            null,
            null,
            null,
            null,
            [],
            "Webhook signature or authorization header was missing or invalid.",
            CancellationToken.None);

        Assert.Equal("webhookTrust", created.FailureCategory);

        var remainingEntries = await db.WebhookDeliveryLogEntries
            .OrderBy(entry => entry.ReceivedAt)
            .ToListAsync();

        Assert.Single(remainingEntries);
        Assert.Equal(created.Id, remainingEntries[0].Id);
    }
}
