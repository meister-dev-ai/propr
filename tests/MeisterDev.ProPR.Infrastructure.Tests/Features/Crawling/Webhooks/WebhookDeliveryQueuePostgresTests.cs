// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling.Webhooks;

/// <summary>
///     The delivery queue against a real PostgreSQL, because what matters here is database behaviour:
///     the claim is one conditional statement with <c>FOR UPDATE SKIP LOCKED</c>, and whether two replicas
///     can take the same delivery is not something an in-memory provider can answer.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class WebhookDeliveryQueuePostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private Guid _configurationId;
    private MeisterProPRDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);

        this._configurationId = await this.SeedConfigurationAsync();
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.WebhookDeliveryQueue
            .Where(entry => entry.WebhookConfigurationId == this._configurationId)
            .ExecuteDeleteAsync();
        await this._dbContext.WebhookConfigurations.Where(c => c.Id == this._configurationId).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => t.Id == this._tenantId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task AnAcceptedDelivery_IsClaimedOnceAndOnlyOnce()
    {
        var queue = this.CreateQueue();
        Assert.True(await queue.EnqueueAsync(this.Submission("delivery-1")));

        var first = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));
        var second = await queue.ClaimNextAsync("replica-b", TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, first!.Attempts);
    }

    // Several drain loops in one replica are the same problem as several replicas, and are answered by the
    // same statement. Claimed in parallel from separate connections, because a sequential pair proves only
    // that the second call saw the first one's committed write.
    [Fact]
    public async Task ConcurrentClaims_EachTakeADifferentDelivery()
    {
        var queue = this.CreateQueue();
        for (var i = 0; i < 8; i++)
        {
            Assert.True(await queue.EnqueueAsync(this.Submission($"delivery-{i}")));
        }

        var claimed = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async slot =>
            {
                await using var context = new MeisterProPRDbContext(
                    new DbContextOptionsBuilder<MeisterProPRDbContext>()
                        .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                        .Options);

                return await new WebhookDeliveryQueue(context)
                    .ClaimNextAsync($"replica-a#{slot}", TimeSpan.FromMinutes(5));
            }));

        var taken = claimed.Where(item => item is not null).Select(item => item!.Id).ToList();

        Assert.Equal(8, taken.Count);
        Assert.Equal(8, taken.Distinct().Count());
        Assert.All(claimed, item => Assert.Equal(1, item!.Attempts));
    }

    // A provider retrying a delivery sends the same key. It must be recognised, or one pull request is
    // reviewed twice and posts two reviews.
    [Fact]
    public async Task ARetriedDelivery_IsRecognisedRatherThanQueuedAgain()
    {
        var queue = this.CreateQueue();

        Assert.True(await queue.EnqueueAsync(this.Submission("delivery-1")));
        Assert.False(await queue.EnqueueAsync(this.Submission("delivery-1")));

        Assert.Equal(
            1, await this._dbContext.WebhookDeliveryQueue
                .CountAsync(entry => entry.WebhookConfigurationId == this._configurationId));
    }

    // Azure DevOps sends no delivery header, so its deliveries have no key. They must still queue —
    // deduplicating on a null key would collapse every ADO delivery into one.
    [Fact]
    public async Task DeliveriesWithoutAKey_AreAllAccepted()
    {
        var queue = this.CreateQueue();

        Assert.True(await queue.EnqueueAsync(this.Submission(null)));
        Assert.True(await queue.EnqueueAsync(this.Submission(null)));

        Assert.Equal(
            2, await this._dbContext.WebhookDeliveryQueue
                .CountAsync(entry => entry.WebhookConfigurationId == this._configurationId));
    }

    [Fact]
    public async Task AFailedDelivery_WaitsForItsBackoffAndIsThenRetried()
    {
        var queue = this.CreateQueue();
        await queue.EnqueueAsync(this.Submission("delivery-1"));
        var claimed = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));

        await queue.FailAsync(claimed!.Id, "provider timed out", maxAttempts: 3, backoff: TimeSpan.FromSeconds(30));

        Assert.Null(await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5)));

        await this.ElapseAsync(TimeSpan.FromSeconds(31));
        var retried = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));

        Assert.NotNull(retried);
        Assert.Equal(2, retried!.Attempts);
    }

    // Kept rather than deleted once it is out of attempts. A delivery that never became a review is the
    // thing an operator has to be able to find, and the payload is what lets them replay it.
    [Fact]
    public async Task ADeliveryOutOfAttempts_IsKeptAsFailedWithItsPayload()
    {
        var queue = this.CreateQueue();
        await queue.EnqueueAsync(this.Submission("delivery-1"));
        var claimed = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));

        await queue.FailAsync(claimed!.Id, "provider timed out", maxAttempts: 1, backoff: TimeSpan.Zero);

        var entry = await this._dbContext.WebhookDeliveryQueue
            .AsNoTracking()
            .SingleAsync(e => e.Id == claimed.Id);

        Assert.Equal(WebhookDeliveryQueueStatus.Failed, entry.Status);
        Assert.Equal("provider timed out", entry.LastError);
        Assert.Equal("{\"pull_request\":42}", entry.Payload);
        Assert.Null(await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5)));
    }

    // A replica that died holding a claim must not strand the review behind it.
    [Fact]
    public async Task AClaimWhoseHolderStopped_IsReturnedWithoutSpendingAnAttempt()
    {
        var queue = this.CreateQueue();
        await queue.EnqueueAsync(this.Submission("delivery-1"));
        var claimed = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));

        await this.ElapseAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await queue.ReleaseExpiredClaimsAsync());

        var retaken = await queue.ClaimNextAsync("replica-b", TimeSpan.FromMinutes(5));
        Assert.NotNull(retaken);
        Assert.Equal(claimed!.Id, retaken!.Id);

        // The first claim counted; the abandonment did not add a second.
        Assert.Equal(2, retaken.Attempts);
    }

    [Fact]
    public async Task AProcessedDelivery_IsNotClaimedAgain()
    {
        var queue = this.CreateQueue();
        await queue.EnqueueAsync(this.Submission("delivery-1"));
        var claimed = await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5));

        await queue.CompleteAsync(claimed!.Id);

        await this.ElapseAsync(TimeSpan.FromHours(1));
        await queue.ReleaseExpiredClaimsAsync();
        Assert.Null(await queue.ClaimNextAsync("replica-a", TimeSpan.FromMinutes(5)));
    }

    // Time passes in the database, because the database's clock is the only one the queue reads: rather
    // than move a host clock it never consults, the waiting rows are aged by moving their eligibility back.
    private Task ElapseAsync(TimeSpan elapsed)
    {
        return this._dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE webhook_delivery_queue
            SET eligible_at = eligible_at - make_interval(secs => {1})
            WHERE webhook_configuration_id = {0}
            """,
            this._configurationId,
            elapsed.TotalSeconds);
    }

    private WebhookDeliveryQueue CreateQueue()
    {
        return new WebhookDeliveryQueue(this._dbContext);
    }

    private WebhookDeliveryQueueSubmission Submission(string? deliveryKey)
    {
        return new WebhookDeliveryQueueSubmission(
            this._configurationId,
            ScmProvider.Forgejo,
            "path-key",
            "pull_request",
            deliveryKey,
            "{\"X-Gitea-Event\":\"pull_request\"}",
            "{\"pull_request\":42}");
    }

    private async Task<Guid> SeedConfigurationAsync()
    {
        var configurationId = Guid.NewGuid();

        await this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO tenants (id, display_name, slug, created_at, updated_at)
            VALUES ({0}, 'queue-tests', {1}, now(), now())
            """,
            this._tenantId,
            "queue-tests-" + this._tenantId.ToString("N")[..8]);

        await this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO clients (id, display_name, is_active, created_at, tenant_id)
            VALUES ({0}, 'queue-tests', true, now(), {1})
            """,
            this._clientId,
            this._tenantId);

        await this._dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO webhook_configurations
                (id, client_id, provider_type, organization_url, project_id, public_path_key,
                 secret_ciphertext, enabled_events, is_active, created_at)
            VALUES ({0}, {1}, 3, 'http://forge.invalid', 'project', 'path-key', 'cipher', '[]', true, now())
            """,
            configurationId,
            this._clientId);

        return configurationId;
    }
}
