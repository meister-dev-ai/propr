// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>Unit tests for <see cref="EfModelPricingResolver" /> using an EF Core in-memory database.</summary>
public sealed class EfModelPricingResolverTests
{
    private static readonly Guid ConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ClientId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static ClientRecord TestClient()
    {
        return new ClientRecord { Id = ClientId, TenantId = TenantId, DisplayName = "Test", IsActive = true };
    }

    private static AiConnectionProfileRecord Profile()
    {
        return new AiConnectionProfileRecord
        {
            Id = ConnectionId,
            ClientId = ClientId,
            DisplayName = "OpenCode Zen",
            ProviderKind = AiProviderKind.OpenAiCompatible.ToString(),
            BaseUrl = "https://opencode.ai/zen/v1",
            AuthMode = AiAuthMode.ApiKey.ToString(),
            DiscoveryMode = AiDiscoveryMode.ManualOnly.ToString(),
            IsActive = true,
        };
    }

    private static AiModelCatalogEntryRecord CatalogEntry(
        string providerId,
        string remoteModelId,
        decimal? input,
        decimal? output,
        Guid? tenantId)
    {
        return new AiModelCatalogEntryRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderId = providerId,
            ProviderName = providerId,
            RemoteModelId = remoteModelId,
            DisplayName = remoteModelId,
            InputCostPer1MUsd = input,
            OutputCostPer1MUsd = output,
            SourceFormat = tenantId is null ? "models.dev" : "operator",
        };
    }

    private static DbContextOptions<MeisterProPRDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"EfModelPricingResolverTests-{Guid.NewGuid():N}")
            .Options;
    }

    private static AiConfiguredModelRecord Model(
        Guid id,
        string remoteModelId,
        string displayName,
        decimal? input,
        decimal? output,
        decimal? cached)
    {
        return new AiConfiguredModelRecord
        {
            Id = id,
            ConnectionProfileId = ConnectionId,
            RemoteModelId = remoteModelId,
            DisplayName = displayName,
            OperationKinds = [AiOperationKind.Chat.ToString()],
            SupportedProtocolModes = [AiProtocolMode.Auto.ToString()],
            Source = AiConfiguredModelSource.Manual.ToString(),
            InputCostPer1MUsd = input,
            OutputCostPer1MUsd = output,
            CachedInputCostPer1MUsd = cached,
        };
    }

    [Fact]
    public async Task ResolveAsync_MatchesByRemoteModelId_ReturnsPricing()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-4o", "GPT-4o", 2.5m, 10m, 1.25m));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.NotNull(pricing);
        Assert.Equal(2.5m, pricing!.InputCostPer1MUsd);
        Assert.Equal(10m, pricing.OutputCostPer1MUsd);
        Assert.Equal(1.25m, pricing.CachedInputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_MatchesByDisplayName_ReturnsPricing()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "deployment-xyz", "gpt-4o", 3m, 12m, null));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.NotNull(pricing);
        Assert.Equal(3m, pricing!.InputCostPer1MUsd);
        Assert.Equal(12m, pricing.OutputCostPer1MUsd);
        Assert.Null(pricing.CachedInputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_NoModelIdMatch_FallsBackToPurposeBinding()
    {
        var options = CreateOptions();
        var boundModelId = Guid.NewGuid();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(boundModelId, "gpt-4o", "GPT-4o", 2m, 8m, 0.5m));
            seed.AiPurposeBindings.Add(
                new AiPurposeBindingRecord
                {
                    Id = Guid.NewGuid(),
                    ConnectionProfileId = ConnectionId,
                    ConfiguredModelId = boundModelId,
                    Purpose = AiPurpose.ReviewLowEffort.ToString(),
                    ProtocolMode = AiProtocolMode.Auto.ToString(),
                    IsEnabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        // Model id does not match any configured model -> purpose binding for LowEffort resolves it.
        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.LowEffort, "unknown-model", default);

        Assert.NotNull(pricing);
        Assert.Equal(2m, pricing!.InputCostPer1MUsd);
        Assert.Equal(8m, pricing.OutputCostPer1MUsd);
        Assert.Equal(0.5m, pricing.CachedInputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_ModelWithoutPricing_ReturnsAllNullRates()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-4o", "GPT-4o", null, null, null));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.NotNull(pricing);
        Assert.Null(pricing!.InputCostPer1MUsd);
        Assert.Null(pricing.OutputCostPer1MUsd);
        Assert.Null(pricing.CachedInputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_EmptyConnectionId_ReturnsNull()
    {
        var resolver = new EfModelPricingResolver(new TestDbContextFactory(CreateOptions()), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(Guid.Empty, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.Null(pricing);
    }

    [Fact]
    public async Task ResolveAsync_NoModelsForConnection_ReturnsNull()
    {
        var resolver = new EfModelPricingResolver(new TestDbContextFactory(CreateOptions()), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.Null(pricing);
    }

    // The case a gateway creates: the endpoint is reached through an OpenAI-compatible profile whose models
    // carry no rate of their own, and the operator has recorded what the gateway actually charges as a scoped
    // catalog entry. Billing that traffic as unpriced made the rate they entered do nothing.
    [Fact]
    public async Task ResolveAsync_ConnectionStatesNoRate_UsesTheOperatorsRecordedRate()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-5.6-luna", "Luna", null, null, null));
            seed.Clients.Add(TestClient());
            seed.AiConnectionProfiles.Add(Profile());
            seed.AiModelCatalogEntries.Add(CatalogEntry("openai", "gpt-5.6-luna", 1m, 6m, tenantId: null));
            seed.AiModelCatalogEntries.Add(CatalogEntry("azure", "gpt-5.6-luna", 1m, 6m, tenantId: null));
            seed.AiModelCatalogEntries.Add(CatalogEntry("openai", "gpt-5.6-luna", 0.2m, 1.2m, TenantId));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.MediumEffort, "gpt-5.6-luna", default);

        Assert.NotNull(pricing);
        Assert.Equal(0.2m, pricing!.InputCostPer1MUsd);
        Assert.Equal(1.2m, pricing.OutputCostPer1MUsd);
    }

    // The connection is the narrowest statement there is, so it is never second-guessed by the catalog.
    [Fact]
    public async Task ResolveAsync_ConnectionStatesItsOwnRate_TheCatalogIsNotConsulted()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-5.6-luna", "Luna", 3m, 9m, null));
            seed.Clients.Add(TestClient());
            seed.AiConnectionProfiles.Add(Profile());
            seed.AiModelCatalogEntries.Add(CatalogEntry("openai", "gpt-5.6-luna", 0.2m, 1.2m, TenantId));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.MediumEffort, "gpt-5.6-luna", default);

        Assert.Equal(3m, pricing!.InputCostPer1MUsd);
    }

    // Falling back to the catalog is not a licence to guess: with no operator entry to settle it, two snapshot
    // providers at different rates leave the model unpriced exactly as before.
    [Fact]
    public async Task ResolveAsync_CatalogDisagreesWithNoOperatorEntry_StaysUnpriced()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-5.6-luna", "Luna", null, null, null));
            seed.Clients.Add(TestClient());
            seed.AiConnectionProfiles.Add(Profile());
            seed.AiModelCatalogEntries.Add(CatalogEntry("openai", "gpt-5.6-luna", 1m, 6m, tenantId: null));
            seed.AiModelCatalogEntries.Add(CatalogEntry("azure", "gpt-5.6-luna", 4m, 20m, tenantId: null));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.MediumEffort, "gpt-5.6-luna", default);

        Assert.Null(pricing!.InputCostPer1MUsd);
        Assert.Null(pricing.OutputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_NoMatchAndNoBinding_ReturnsNull()
    {
        var options = CreateOptions();
        await using (var seed = new MeisterProPRDbContext(options))
        {
            seed.AiConfiguredModels.Add(Model(Guid.NewGuid(), "gpt-4o", "GPT-4o", 2m, 8m, null));
            await seed.SaveChangesAsync();
        }

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options), TimeProvider.System);

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.MediumEffort, "unknown-model", default);

        Assert.Null(pricing);
    }
}
