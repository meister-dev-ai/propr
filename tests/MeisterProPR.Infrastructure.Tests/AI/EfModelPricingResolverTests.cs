// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>Unit tests for <see cref="EfModelPricingResolver" /> using an EF Core in-memory database.</summary>
public sealed class EfModelPricingResolverTests
{
    private static readonly Guid ConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

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

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options));

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

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options));

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

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options));

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

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options));

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.NotNull(pricing);
        Assert.Null(pricing!.InputCostPer1MUsd);
        Assert.Null(pricing.OutputCostPer1MUsd);
        Assert.Null(pricing.CachedInputCostPer1MUsd);
    }

    [Fact]
    public async Task ResolveAsync_EmptyConnectionId_ReturnsNull()
    {
        var resolver = new EfModelPricingResolver(new TestDbContextFactory(CreateOptions()));

        var pricing = await resolver.ResolveAsync(Guid.Empty, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.Null(pricing);
    }

    [Fact]
    public async Task ResolveAsync_NoModelsForConnection_ReturnsNull()
    {
        var resolver = new EfModelPricingResolver(new TestDbContextFactory(CreateOptions()));

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.HighEffort, "gpt-4o", default);

        Assert.Null(pricing);
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

        var resolver = new EfModelPricingResolver(new TestDbContextFactory(options));

        var pricing = await resolver.ResolveAsync(ConnectionId, AiConnectionModelCategory.MediumEffort, "unknown-model", default);

        Assert.Null(pricing);
    }
}
