// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Covers catalog import against a real PostgreSQL instance. The properties worth proving are the ones that
///     only show up on a second run or with an override present: import must be safely repeatable, and it must
///     leave scoped overrides alone.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ModelCatalogImportServiceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly string _providerId = "prov-" + Guid.NewGuid().ToString("N")[..8];
    private readonly StubTimeProvider _time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
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
    public async Task ImportWritesGlobalEntries()
    {
        var written = await this.Sut().ImportSnapshotAsync(Snapshot(inputCost: 1.0m));

        Assert.Equal(1, written);
        var row = await this.SingleGlobalRow();
        Assert.Null(row.TenantId);
        Assert.Null(row.ClientId);
        Assert.Equal("models.dev", row.SourceFormat);
        Assert.Equal(1.0m, row.InputCostPer1MUsd);
        Assert.Equal(this._time.GetUtcNow(), row.ImportedAt, TimeSpan.FromSeconds(1));
    }

    // Startup runs the seed every time, so a second import must update in place rather than duplicate. The
    // partial unique index would reject a duplicate anyway; this proves we never attempt one.
    [Fact]
    public async Task ImportingTwice_UpdatesInPlaceRatherThanDuplicating()
    {
        await this.Sut().ImportSnapshotAsync(Snapshot(inputCost: 1.0m));
        var firstId = (await this.SingleGlobalRow()).Id;

        this._time.Advance(TimeSpan.FromHours(1));
        var written = await this.Sut().ImportSnapshotAsync(Snapshot(inputCost: 2.0m));

        Assert.Equal(1, written);
        var row = await this.SingleGlobalRow();
        Assert.Equal(firstId, row.Id);
        Assert.Equal(2.0m, row.InputCostPer1MUsd);
        Assert.Equal(this._time.GetUtcNow(), row.ImportedAt, TimeSpan.FromSeconds(1));
    }

    // The reason import is scoped to global rows: a tenant's negotiated price is the whole point of the override
    // layer, and a refresh that reset it would silently re-price that customer at list.
    [Fact]
    public async Task ImportLeavesATenantOverrideUntouched()
    {
        await this.Sut().ImportSnapshotAsync(Snapshot(inputCost: 1.0m));

        this._dbContext.AiModelCatalogEntries.Add(
            new AiModelCatalogEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = this._tenantId,
                ProviderId = this._providerId,
                ProviderName = "Test Provider",
                RemoteModelId = "test-model",
                DisplayName = "Negotiated",
                InputCostPer1MUsd = 0.10m,
                SourceFormat = "operator",
                ImportedAt = this._time.GetUtcNow(),
            });
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        await this.Sut().ImportSnapshotAsync(Snapshot(inputCost: 9.0m));
        this._dbContext.ChangeTracker.Clear();

        var tenantRow = await this._dbContext.AiModelCatalogEntries
            .SingleAsync(x => x.ProviderId == this._providerId && x.TenantId == this._tenantId);
        Assert.Equal(0.10m, tenantRow.InputCostPer1MUsd);
        Assert.Equal("Negotiated", tenantRow.DisplayName);
        Assert.Equal("operator", tenantRow.SourceFormat);

        // The global row still tracks the snapshot.
        Assert.Equal(9.0m, (await this.SingleGlobalRow()).InputCostPer1MUsd);
    }

    [Fact]
    public async Task EmptySnapshotWritesNothing()
    {
        Assert.Equal(0, await this.Sut().ImportSnapshotAsync(new MemoryStream(Encoding.UTF8.GetBytes("{}"))));
    }

    private ModelCatalogImportService Sut()
    {
        return new ModelCatalogImportService(this._dbContext, new ModelsDevCatalogSnapshotImporter(), this._time);
    }

    private async Task<AiModelCatalogEntryRecord> SingleGlobalRow()
    {
        this._dbContext.ChangeTracker.Clear();
        return await this._dbContext.AiModelCatalogEntries
            .SingleAsync(x => x.ProviderId == this._providerId && x.TenantId == null && x.ClientId == null);
    }

    // A minimal controllable clock; the project has no fake-time package and one assertion does not justify adding one.
    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => this._now;

        public void Advance(TimeSpan by) => this._now = this._now.Add(by);
    }

    private Stream Snapshot(decimal inputCost)
    {
        var json = $$"""
                     {
                       "{{this._providerId}}": {
                         "id": "{{this._providerId}}",
                         "name": "Test Provider",
                         "models": {
                           "test-model": {
                             "id": "test-model",
                             "name": "Test Model",
                             "reasoning": true,
                             "tool_call": true,
                             "interleaved": { "field": "reasoning_content" },
                             "limit": { "context": 1024, "output": 512 },
                             "cost": { "input": {{inputCost}}, "output": 3.0, "cache_read": 0.1 }
                           }
                         }
                       }
                     }
                     """;
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }
}
