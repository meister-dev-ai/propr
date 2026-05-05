// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>Integration tests for webhook configuration and delivery-history repositories against PostgreSQL.</summary>
[Collection("PostgresIntegration")]
public sealed class WebhookConfigurationRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private Guid _clientId;
    private EfWebhookConfigurationRepository _configRepo = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private EfWebhookDeliveryLogRepository _logRepo = null!;
    private Guid _otherClientId;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        this._clientId = Guid.NewGuid();
        this._otherClientId = Guid.NewGuid();

        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Webhook Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._otherClientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Other Webhook Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        await this._dbContext.SaveChangesAsync();

        this._configRepo = new EfWebhookConfigurationRepository(this._dbContext);
        this._logRepo = new EfWebhookDeliveryLogRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        var clientIds = new[] { this._clientId, this._otherClientId };
        var configIds = await this._dbContext.WebhookConfigurations
            .Where(config => clientIds.Contains(config.ClientId))
            .Select(config => config.Id)
            .ToListAsync();

        if (configIds.Count > 0)
        {
            await this._dbContext.WebhookDeliveryLogEntries
                .Where(entry => configIds.Contains(entry.WebhookConfigurationId))
                .ExecuteDeleteAsync();
            await this._dbContext.WebhookRepoFilters
                .Where(filter => configIds.Contains(filter.WebhookConfigurationId))
                .ExecuteDeleteAsync();
            await this._dbContext.WebhookConfigurations
                .Where(config => configIds.Contains(config.Id))
                .ExecuteDeleteAsync();
        }

        await this._dbContext.Clients
            .Where(client => clientIds.Contains(client.Id))
            .ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task AddAsync_PersistsConfigurationAndReturnsDto()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-1",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCreated, WebhookEventType.PullRequestUpdated],
            null,
            CancellationToken.None,
            reviewTemperature: 0.15f);

        var stored = await this._dbContext.WebhookConfigurations.SingleAsync(config => config.Id == created.Id);

        Assert.Equal(this._clientId, created.ClientId);
        Assert.Equal("path-key-1", created.PublicPathKey);
        Assert.Equal([WebhookEventType.PullRequestCreated, WebhookEventType.PullRequestUpdated], created.EnabledEvents);
        Assert.Equal(0.15f, created.ReviewTemperature);
        Assert.Equal("ciphertext", stored.SecretCiphertext);
        Assert.Equal(0.15f, stored.ReviewTemperature);
    }

    [Fact]
    public async Task UpdateAsync_PersistsReviewTemperature()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-temp",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCreated],
            null,
            CancellationToken.None);

        var updated = await this._configRepo.UpdateAsync(
            created.Id,
            null,
            null,
            this._clientId,
            CancellationToken.None,
            reviewTemperature: 0.35f,
            shouldUpdateReviewTemperature: true);

        var stored = await this._dbContext.WebhookConfigurations.SingleAsync(config => config.Id == created.Id);
        var fetched = await this._configRepo.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.True(updated);
        Assert.Equal(0.35f, stored.ReviewTemperature);
        Assert.NotNull(fetched);
        Assert.Equal(0.35f, fetched.ReviewTemperature);
    }

    [Fact]
    public async Task UpdateAsync_ClearsReviewTemperature_WhenExplicitlySpecifiedAsNull()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-temp-clear",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCreated],
            null,
            CancellationToken.None,
            reviewTemperature: 0.35f);

        var updated = await this._configRepo.UpdateAsync(
            created.Id,
            null,
            null,
            this._clientId,
            CancellationToken.None,
            reviewTemperature: null,
            shouldUpdateReviewTemperature: true);

        var stored = await this._dbContext.WebhookConfigurations.SingleAsync(config => config.Id == created.Id);
        var fetched = await this._configRepo.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.True(updated);
        Assert.Null(stored.ReviewTemperature);
        Assert.NotNull(fetched);
        Assert.Null(fetched.ReviewTemperature);
    }

    [Fact]
    public async Task GetByIdAsync_IgnoresUnexpectedEnabledEventsStoredInDatabase()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-1b",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCreated],
            null,
            CancellationToken.None);

        var record = await this._dbContext.WebhookConfigurations.SingleAsync(config => config.Id == created.Id);
        record.EnabledEvents = ["PullRequestCreated", "DefinitelyNotARealEvent"];
        await this._dbContext.SaveChangesAsync();

        var fetched = await this._configRepo.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal([WebhookEventType.PullRequestCreated], fetched.EnabledEvents);
    }

    [Fact]
    public async Task GetActiveByPathKeyAsync_ReturnsOnlyActiveConfiguration()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-2",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCreated],
            null,
            CancellationToken.None);

        var fetched = await this._configRepo.GetActiveByPathKeyAsync("path-key-2", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        await this._configRepo.UpdateAsync(created.Id, false, null, this._clientId, CancellationToken.None);

        var inactiveFetch = await this._configRepo.GetActiveByPathKeyAsync("path-key-2", CancellationToken.None);
        Assert.Null(inactiveFetch);
    }

    [Fact]
    public async Task UpdateRepoFiltersAsync_PersistsCanonicalFilterMetadata()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-3",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestCommented],
            null,
            CancellationToken.None);

        var updated = await this._configRepo.UpdateRepoFiltersAsync(
            created.Id,
            [
                new WebhookRepoFilterDto(
                    Guid.Empty,
                    "meister-propr",
                    ["main", "release/*"],
                    new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                    "Meister ProPR"),
            ],
            CancellationToken.None);

        var stored =
            await this._dbContext.WebhookRepoFilters.SingleAsync(filter => filter.WebhookConfigurationId == created.Id);

        Assert.True(updated);
        Assert.Equal("azureDevOps", stored.SourceProvider);
        Assert.Equal("repo-1", stored.CanonicalSourceRef);
        Assert.Equal(["main", "release/*"], stored.TargetBranchPatterns);
    }

    [Fact]
    public async Task AddAsync_DeliveryHistory_PersistsAndListsMostRecentFirst()
    {
        var created = await this._configRepo.AddAsync(
            this._clientId,
            WebhookProviderType.AzureDevOps,
            "path-key-4",
            "https://dev.azure.com/org",
            "project",
            "ciphertext",
            [WebhookEventType.PullRequestUpdated],
            null,
            CancellationToken.None);

        await this._logRepo.AddAsync(
            created.Id,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "git.pullrequest.updated",
            WebhookDeliveryOutcome.Accepted,
            200,
            "repo-1",
            42,
            "refs/heads/feature/test",
            "refs/heads/main",
            ["Submitted review intake refresh"],
            null,
            CancellationToken.None);

        await this._logRepo.AddAsync(
            created.Id,
            DateTimeOffset.UtcNow,
            "git.pullrequest.updated",
            WebhookDeliveryOutcome.Ignored,
            200,
            "repo-1",
            42,
            "refs/heads/feature/test",
            "refs/heads/main",
            [],
            null,
            CancellationToken.None);

        var entries = await this._logRepo.ListByWebhookConfigurationAsync(created.Id, 10, CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal(WebhookDeliveryOutcome.Ignored, entries[0].DeliveryOutcome);
        Assert.Equal(WebhookDeliveryOutcome.Accepted, entries[1].DeliveryOutcome);
        Assert.Equal("Submitted review intake refresh", entries[1].ActionSummaries[0]);
    }
}
