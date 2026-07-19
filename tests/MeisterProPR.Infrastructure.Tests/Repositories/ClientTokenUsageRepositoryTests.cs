// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="ClientTokenUsageRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClientTokenUsageRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private Guid _clientId;
    private MeisterProPRDbContext _dbContext = null!;
    private IClientTokenUsageRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Seed a client for FK constraint
        this._clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Test Client for Token Usage",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();

        this._repo = new ClientTokenUsageRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        // Clean up samples and client seeded for this test run
        await this._dbContext.ClientTokenUsageSamples
            .Where(s => s.ClientId == this._clientId)
            .ExecuteDeleteAsync();
        await this._dbContext.Clients
            .Where(c => c.Id == this._clientId)
            .ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    // T053: calling upsert twice for the same (ClientId, ModelId, Date) accumulates tokens
    [Fact]
    public async Task UpsertAsync_AccumulatesTokens_ForSameClientModelDay()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string modelId = "gpt-4o";

        await this._repo.UpsertAsync(this._clientId, modelId, date, 100, 50, default, 40, 0, 20);
        await this._repo.UpsertAsync(this._clientId, modelId, date, 200, 75, default, 60, 0, 30);

        var sample = await this._dbContext.ClientTokenUsageSamples
            .SingleAsync(s => s.ClientId == this._clientId && s.ModelId == modelId && s.Date == date);

        Assert.Equal(300, sample.InputTokens);
        Assert.Equal(125, sample.OutputTokens);
        Assert.Equal(100, sample.CachedInputTokens);
        Assert.Equal(0, sample.CacheWriteTokens);
        Assert.Equal(50, sample.ReasoningTokens);
    }

    [Fact]
    public async Task UpsertAsync_AccumulatesEstimatedCost_ForSameClientModelDay()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string modelId = "gpt-4o";

        await this._repo.UpsertAsync(this._clientId, modelId, date, 100, 50, default, estimatedCostUsd: 0.10m);
        await this._repo.UpsertAsync(this._clientId, modelId, date, 200, 75, default, estimatedCostUsd: 0.05m);

        var sample = await this._dbContext.ClientTokenUsageSamples
            .SingleAsync(s => s.ClientId == this._clientId && s.ModelId == modelId && s.Date == date);

        Assert.Equal(0.15m, sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task UpsertAsync_NullCostOnNewRow_LeavesEstimatedCostNull()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string modelId = "unpriced-model";

        await this._repo.UpsertAsync(this._clientId, modelId, date, 100, 50, default, estimatedCostUsd: null);

        var sample = await this._dbContext.ClientTokenUsageSamples
            .SingleAsync(s => s.ClientId == this._clientId && s.ModelId == modelId && s.Date == date);

        Assert.Null(sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task UpsertAsync_RepeatedNullCost_KeepsEstimatedCostNull()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string modelId = "unpriced-model";

        // An unpriced model reviewed twice on the same day must stay "pricing unknown" (null),
        // not collapse into a misleading real zero on the second (ON CONFLICT) contribution.
        await this._repo.UpsertAsync(this._clientId, modelId, date, 100, 50, default, estimatedCostUsd: null);
        await this._repo.UpsertAsync(this._clientId, modelId, date, 200, 75, default, estimatedCostUsd: null);

        var sample = await this._dbContext.ClientTokenUsageSamples
            .SingleAsync(s => s.ClientId == this._clientId && s.ModelId == modelId && s.Date == date);

        Assert.Null(sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task UpsertAsync_PricedThenNullCost_KeepsPricedValue()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string modelId = "gpt-4o";

        // A priced contribution followed by an unpriced one keeps the known spend (non-null),
        // treating the unknown delta as zero rather than nullifying the whole day.
        await this._repo.UpsertAsync(this._clientId, modelId, date, 100, 50, default, estimatedCostUsd: 0.10m);
        await this._repo.UpsertAsync(this._clientId, modelId, date, 200, 75, default, estimatedCostUsd: null);

        var sample = await this._dbContext.ClientTokenUsageSamples
            .SingleAsync(s => s.ClientId == this._clientId && s.ModelId == modelId && s.Date == date);

        Assert.Equal(0.10m, sample.EstimatedCostUsd);
    }

    [Fact]
    public async Task GetRecentTotalsByClientAsync_SumsInputPlusOutputWithinRangeOnly()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var inRange = today.AddDays(-5);
        var alsoInRange = today.AddDays(-20);
        var outOfRange = today.AddDays(-40);

        await this._repo.UpsertAsync(this._clientId, "gpt-4o", inRange, 100, 50, default);
        await this._repo.UpsertAsync(this._clientId, "gpt-4o-mini", inRange, 200, 25, default);
        await this._repo.UpsertAsync(this._clientId, "gpt-4o", alsoInRange, 10, 5, default);
        await this._repo.UpsertAsync(this._clientId, "gpt-4o", outOfRange, 9999, 9999, default);

        var totals = await this._repo.GetRecentTotalsByClientAsync(today.AddDays(-30), today, default);

        // (100+50) + (200+25) + (10+5) = 390; the 40-day-old sample falls outside the window.
        Assert.Equal(390L, totals[this._clientId]);
        // A client with no samples in the range is absent from the result.
        Assert.False(totals.ContainsKey(Guid.NewGuid()));
    }

    // T054: two different models on the same day produce two separate rows
    [Fact]
    public async Task UpsertAsync_CreatesNewRow_WhenDifferentModel()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        const string model1 = "gpt-5-mini";
        const string model2 = "text-embedding-3-small";

        await this._repo.UpsertAsync(this._clientId, model1, date, 100, 50, default);
        await this._repo.UpsertAsync(this._clientId, model2, date, 200, 75, default);

        var samples = await this._dbContext.ClientTokenUsageSamples
            .Where(s => s.ClientId == this._clientId && s.Date == date)
            .ToListAsync();

        Assert.Equal(2, samples.Count);
        Assert.Contains(samples, s => s.ModelId == model1 && s.InputTokens == 100);
        Assert.Contains(samples, s => s.ModelId == model2 && s.InputTokens == 200);
    }
}
