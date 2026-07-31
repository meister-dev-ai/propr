// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Verifies the catalog table against a real PostgreSQL instance, because the properties that matter here are
///     database behaviour rather than model configuration: the scope uniqueness relies on PARTIAL indexes, which
///     exist precisely because PostgreSQL treats NULLs in a plain unique index as distinct and would otherwise
///     accept duplicate global rows.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AiModelCatalogEntrySchemaTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly string _providerId = "prov-" + Guid.NewGuid().ToString("N")[..8];
    private MeisterProPRDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.AiModelCatalogEntries.Where(x => x.ProviderId == this._providerId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task EveryColumnRoundTrips()
    {
        var entry = Entry();
        entry.Family = "deepseek";
        entry.SupportsToolUse = true;
        entry.SupportsStructuredOutput = true;
        entry.SupportsReasoning = true;
        entry.SupportsPromptCaching = true;
        entry.ReasoningContentField = "reasoning_content";
        entry.MaxContextTokens = 131072;
        entry.MaxOutputTokens = 65536;
        entry.InputCostPer1MUsd = 0.28m;
        entry.OutputCostPer1MUsd = 0.42m;
        entry.CachedInputCostPer1MUsd = 0.028m;
        entry.CacheWriteCostPer1MUsd = 0.14m;
        entry.OpenWeights = true;
        entry.ReleaseDate = new DateOnly(2026, 1, 20);

        this._dbContext.AiModelCatalogEntries.Add(entry);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        var read = await this._dbContext.AiModelCatalogEntries.SingleAsync(x => x.Id == entry.Id);
        Assert.Equal("deepseek", read.Family);
        Assert.True(read.SupportsReasoning);
        Assert.True(read.SupportsPromptCaching);
        Assert.Equal("reasoning_content", read.ReasoningContentField);
        Assert.Equal(131072, read.MaxContextTokens);
        // Decimal precision must survive: a rounded cache-read price would misprice a cap.
        Assert.Equal(0.028m, read.CachedInputCostPer1MUsd);
        Assert.Equal(0.14m, read.CacheWriteCostPer1MUsd);
        Assert.Equal(new DateOnly(2026, 1, 20), read.ReleaseDate);
        Assert.True(read.OpenWeights);
    }

    // The whole reason the global index is partial. A plain composite unique index over the nullable owner
    // columns would treat two all-NULL rows as distinct and let a refresh duplicate every global entry.
    [Fact]
    public async Task DuplicateGlobalRowsForOneModel_AreRejected()
    {
        this._dbContext.AiModelCatalogEntries.Add(Entry());
        await this._dbContext.SaveChangesAsync();

        this._dbContext.AiModelCatalogEntries.Add(Entry());

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => this._dbContext.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        this._dbContext.ChangeTracker.Clear();
    }

    // A tenant and a client override of the same model coexist with the global row: they are different scopes,
    // which is what makes the override layering possible at all.
    [Fact]
    public async Task GlobalRowAndScopedOverridesForTheSameModel_Coexist()
    {
        var global = Entry();
        var tenantOverride = Entry();
        tenantOverride.TenantId = this._tenantId;
        tenantOverride.InputCostPer1MUsd = 0.10m;
        var clientOverride = Entry();
        clientOverride.ClientId = this._clientId;
        clientOverride.InputCostPer1MUsd = 0.05m;

        this._dbContext.AiModelCatalogEntries.AddRange(global, tenantOverride, clientOverride);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        var rows = await this._dbContext.AiModelCatalogEntries
            .Where(x => x.ProviderId == this._providerId)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Single(rows, x => x.TenantId == null && x.ClientId == null);
        Assert.Single(rows, x => x.TenantId == this._tenantId);
        Assert.Single(rows, x => x.ClientId == this._clientId);
    }

    [Fact]
    public async Task DuplicateOverridesWithinOneScope_AreRejected()
    {
        var first = Entry();
        first.TenantId = this._tenantId;
        this._dbContext.AiModelCatalogEntries.Add(first);
        await this._dbContext.SaveChangesAsync();

        var second = Entry();
        second.TenantId = this._tenantId;
        this._dbContext.AiModelCatalogEntries.Add(second);

        await Assert.ThrowsAsync<DbUpdateException>(() => this._dbContext.SaveChangesAsync());
        this._dbContext.ChangeTracker.Clear();
    }

    private AiModelCatalogEntryRecord Entry()
    {
        return new AiModelCatalogEntryRecord
        {
            Id = Guid.NewGuid(),
            ProviderId = this._providerId,
            ProviderName = "Test Provider",
            RemoteModelId = "test-model",
            DisplayName = "Test Model",
            SourceFormat = "models.dev",
            ImportedAt = DateTimeOffset.UtcNow,
        };
    }
}
