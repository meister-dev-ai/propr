// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Covers how the catalog's three scope layers resolve into what one client sees. The rules that matter are
///     the precedence of a narrower override, the asymmetry between price and capability, and that a sibling
///     tenant's rates are never visible.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ModelCatalogRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();
    private readonly Guid _otherClient = Guid.NewGuid();
    private readonly string _providerId = "prov-" + Guid.NewGuid().ToString("N")[..8];
    private MeisterProPRDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options);

        var now = DateTimeOffset.UtcNow;
        this._dbContext.Tenants.Add(Tenant(this._tenantId, now));
        this._dbContext.Tenants.Add(Tenant(this._otherTenantId, now));
        this._dbContext.Clients.Add(Client(this._clientA, this._tenantId, now));
        this._dbContext.Clients.Add(Client(this._clientB, this._tenantId, now));
        this._dbContext.Clients.Add(Client(this._otherClient, this._otherTenantId, now));
        await this._dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        Guid[] clients = [this._clientA, this._clientB, this._otherClient];
        Guid[] tenants = [this._tenantId, this._otherTenantId];
        await this._dbContext.AiModelCatalogEntries.Where(x => x.ProviderId == this._providerId).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(c => clients.Contains(c.Id)).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => tenants.Contains(t.Id)).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task WithNoOverrides_TheGlobalSnapshotApplies()
    {
        await this.Seed(Global(input: 10m));

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));

        Assert.Equal(10m, entry.InputCostPer1MUsd);
        Assert.Equal(AiModelCatalogLayer.Global, entry.PricingLayer);
    }

    // The reason the override layer exists: a tenant on a negotiated rate must not be priced at list.
    [Fact]
    public async Task ATenantOverrideBeatsTheGlobalPrice_ForEveryClientInThatTenant()
    {
        await this.Seed(Global(input: 10m), TenantOverride(this._tenantId, input: 4m));

        foreach (var clientId in new[] { this._clientA, this._clientB })
        {
            var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(clientId, this._providerId));
            Assert.Equal(4m, entry.InputCostPer1MUsd);
            Assert.Equal(AiModelCatalogLayer.TenantOverride, entry.PricingLayer);
        }
    }

    [Fact]
    public async Task AClientOverrideBeatsItsTenantsOverride_AndOnlyForThatClient()
    {
        await this.Seed(Global(input: 10m), TenantOverride(this._tenantId, input: 4m), ClientOverride(this._clientA, input: 2m));

        var forA = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.Equal(2m, forA.InputCostPer1MUsd);
        Assert.Equal(AiModelCatalogLayer.ClientOverride, forA.PricingLayer);

        // A sibling in the same tenant still sees the tenant rate, not its neighbour's.
        var forB = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientB, this._providerId));
        Assert.Equal(4m, forB.InputCostPer1MUsd);
        Assert.Equal(AiModelCatalogLayer.TenantOverride, forB.PricingLayer);
    }

    // Isolation, not merely non-use: another tenant's negotiated rate must not reach this client at all.
    [Fact]
    public async Task AnotherTenantsOverrideIsInvisible()
    {
        await this.Seed(Global(input: 10m), TenantOverride(this._otherTenantId, input: 1m));

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));

        Assert.Equal(10m, entry.InputCostPer1MUsd);
        Assert.Equal(AiModelCatalogLayer.Global, entry.PricingLayer);
    }

    // Price can differ per customer; a capability cannot. Whether a model supports tool use is a fact about the
    // model, so an override must not be able to claim otherwise.
    [Fact]
    public async Task CapabilitiesComeFromTheSnapshotEvenWhenAnOverrideDisagrees()
    {
        var global = Global(input: 10m);
        global.SupportsToolUse = true;
        global.SupportsReasoning = true;
        global.MaxContextTokens = 200_000;
        global.ReasoningContentField = "reasoning_content";

        var tenantRow = TenantOverride(this._tenantId, input: 4m);
        tenantRow.SupportsToolUse = false;
        tenantRow.SupportsReasoning = false;
        tenantRow.MaxContextTokens = 8;
        tenantRow.ReasoningContentField = null;

        await this.Seed(global, tenantRow);

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.True(entry.SupportsToolUse);
        Assert.True(entry.SupportsReasoning);
        Assert.Equal(200_000, entry.MaxContextTokens);
        Assert.Equal("reasoning_content", entry.ReasoningContentField);
        // Price still comes from the override.
        Assert.Equal(4m, entry.InputCostPer1MUsd);
    }

    // A null in an override means "inherit", not "free" — conflating them would under-bill a cap.
    [Fact]
    public async Task AnOverrideStatingOnlyOnePrice_InheritsTheRest()
    {
        var tenantRow = TenantOverride(this._tenantId, input: 4m);
        tenantRow.OutputCostPer1MUsd = null;

        await this.Seed(Global(input: 10m, output: 30m), tenantRow);

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.Equal(4m, entry.InputCostPer1MUsd);
        Assert.Equal(30m, entry.OutputCostPer1MUsd);
    }

    // An operator-defined model has no snapshot row to merge onto, so it stands alone and supplies its own facts.
    [Fact]
    public async Task AnOperatorDefinedModelWithNoGlobalRow_ResolvesOnItsOwn()
    {
        var tenantRow = TenantOverride(this._tenantId, input: 7m);
        tenantRow.RemoteModelId = "private-finetune";
        tenantRow.DisplayName = "Private Finetune";
        tenantRow.SupportsToolUse = true;
        tenantRow.MaxContextTokens = 32_000;

        await this.Seed(tenantRow);

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.Equal("private-finetune", entry.RemoteModelId);
        Assert.Equal("Private Finetune", entry.DisplayName);
        Assert.True(entry.SupportsToolUse);
        Assert.Equal(32_000, entry.MaxContextTokens);
        Assert.Equal(7m, entry.InputCostPer1MUsd);
    }

    [Fact]
    public async Task GetProvidersCountsGlobalEntriesOnly()
    {
        var second = Global(input: 1m);
        second.RemoteModelId = "second-model";
        await this.Seed(Global(input: 10m), second, TenantOverride(this._tenantId, input: 4m));

        var provider = Assert.Single(await this.Sut().GetProvidersAsync(), p => p.ProviderId == this._providerId);

        // Two global models; the override is a scoped view of one of them, not a third model.
        Assert.Equal(2, provider.ModelCount);
    }

    // An operator-defined model is the answer to a model the snapshot has never heard of, so it must become
    // selectable with the facts the operator supplied.
    [Fact]
    public async Task ADefinedModelIsResolvableWithItsOwnFacts()
    {
        await this.Sut().UpsertTenantModelDefinitionAsync(
            this._tenantId,
            new AiModelCatalogDefinitionDto(
                this._providerId,
                "private-finetune",
                DisplayName: "Private Finetune",
                SupportsToolUse: true,
                SupportsReasoning: true,
                ReasoningContentField: "reasoning_content",
                MaxContextTokens: 32_000,
                InputCostPer1MUsd: 7m));

        var entry = Assert.Single(await this.Sut().GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.Equal("private-finetune", entry.RemoteModelId);
        Assert.Equal("Private Finetune", entry.DisplayName);
        Assert.True(entry.SupportsToolUse);
        Assert.True(entry.SupportsReasoning);
        Assert.Equal("reasoning_content", entry.ReasoningContentField);
        Assert.Equal(32_000, entry.MaxContextTokens);
        Assert.Equal(7m, entry.InputCostPer1MUsd);
    }

    [Fact]
    public async Task ADefinedModelIsInvisibleToAnotherTenant()
    {
        await this.Sut().UpsertTenantModelDefinitionAsync(
            this._tenantId,
            new AiModelCatalogDefinitionDto(this._providerId, "private-finetune", InputCostPer1MUsd: 7m));

        Assert.Empty(await this.Sut().GetEffectiveForClientAsync(this._otherClient, this._providerId));
    }

    // Defining a model the snapshot already describes would silently discard the capabilities just entered,
    // because the snapshot supplies them. Refusing says so instead.
    [Fact]
    public async Task DefiningAModelTheSnapshotAlreadyDescribes_IsRefused()
    {
        await this.Seed(Global(input: 10m));

        var exception = await Assert.ThrowsAsync<ModelCatalogDefinitionConflictException>(() => this.Sut().UpsertTenantModelDefinitionAsync(
            this._tenantId,
            new AiModelCatalogDefinitionDto(this._providerId, "test-model", SupportsToolUse: true)));

        Assert.Contains("pricing override", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedefiningAModelUpdatesItInPlace()
    {
        var sut = this.Sut();
        await sut.UpsertTenantModelDefinitionAsync(
            this._tenantId,
            new AiModelCatalogDefinitionDto(this._providerId, "private-finetune", MaxContextTokens: 8_000));

        await sut.UpsertTenantModelDefinitionAsync(
            this._tenantId,
            new AiModelCatalogDefinitionDto(this._providerId, "private-finetune", MaxContextTokens: 64_000));

        var entry = Assert.Single(await sut.GetEffectiveForClientAsync(this._clientA, this._providerId));
        Assert.Equal(64_000, entry.MaxContextTokens);
    }

    private ModelCatalogRepository Sut() => new(this._dbContext, TimeProvider.System);

    private async Task Seed(params AiModelCatalogEntryRecord[] rows)
    {
        this._dbContext.AiModelCatalogEntries.AddRange(rows);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();
    }

    private AiModelCatalogEntryRecord Global(decimal? input = null, decimal? output = null) =>
        this.Row(null, null, input, output);

    private AiModelCatalogEntryRecord TenantOverride(Guid tenantId, decimal? input = null, decimal? output = null) =>
        this.Row(tenantId, null, input, output);

    private AiModelCatalogEntryRecord ClientOverride(Guid clientId, decimal? input = null, decimal? output = null) =>
        this.Row(null, clientId, input, output);

    private AiModelCatalogEntryRecord Row(Guid? tenantId, Guid? clientId, decimal? input, decimal? output)
    {
        return new AiModelCatalogEntryRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            ProviderId = this._providerId,
            ProviderName = "Test Provider",
            RemoteModelId = "test-model",
            DisplayName = "Test Model",
            InputCostPer1MUsd = input,
            OutputCostPer1MUsd = output,
            SourceFormat = "models.dev",
            ImportedAt = DateTimeOffset.UtcNow,
        };
    }

    private static TenantRecord Tenant(Guid id, DateTimeOffset now) => new()
    {
        Id = id,
        Slug = "t-" + id.ToString("N")[..8],
        DisplayName = "Tenant",
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static ClientRecord Client(Guid id, Guid tenantId, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        DisplayName = "Model Catalog Test Client",
        IsActive = true,
        CreatedAt = now,
    };
}
