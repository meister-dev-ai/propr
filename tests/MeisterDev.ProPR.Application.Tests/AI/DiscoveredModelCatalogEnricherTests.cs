// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>
///     A model-list endpoint returns identifiers, not economics — several return nothing but an id — so a
///     discovered model arrives unpriced and a budget cap would be enforced against zero. These cover the merge
///     that fixes that, and the case where guessing would be worse than leaving the field blank.
/// </summary>
public sealed class DiscoveredModelCatalogEnricherTests
{
    [Fact]
    public void AModelTheCatalogKnowsIsPricedAndDescribed()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("deepseek-v4-flash")),
            [Catalog("opencode", "deepseek-v4-flash", input: 0.14m, output: 0.28m, context: 200_000)]);

        var model = Assert.Single(result.Models);
        Assert.Equal(0.14m, model.InputCostPer1MUsd);
        Assert.Equal(0.28m, model.OutputCostPer1MUsd);
        Assert.Equal(200_000, model.MaxContextTokens);
        Assert.Contains(result.Warnings, warning => warning.Contains("'opencode'", StringComparison.Ordinal));
    }

    // Picking one of several would silently bill a gateway's traffic at the underlying vendor's rate. The
    // browse-and-pick surface knows which provider the operator meant; this does not, so it declines to guess.
    [Fact]
    public void AModelSeveralProvidersSellAtDifferentRatesIsLeftUnpriced()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("deepseek-v4-pro")),
            [
                Catalog("deepseek", "deepseek-v4-pro", input: 0.435m, output: 0.87m),
                Catalog("opencode", "deepseek-v4-pro", input: 1.74m, output: 3.84m),
            ]);

        var model = Assert.Single(result.Models);
        Assert.Null(model.InputCostPer1MUsd);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("deepseek", StringComparison.Ordinal)
                       && warning.Contains("opencode", StringComparison.Ordinal));
    }

    // Same model, same price, two providers: nothing is ambiguous about the answer, so it is still applied.
    [Fact]
    public void AModelSeveralProvidersSellAtTheSameRateIsStillPriced()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("deepseek-v4-flash")),
            [
                Catalog("deepseek", "deepseek-v4-flash", input: 0.14m, output: 0.28m),
                Catalog("opencode", "deepseek-v4-flash", input: 0.14m, output: 0.28m),
            ]);

        Assert.Equal(0.14m, Assert.Single(result.Models).InputCostPer1MUsd);
    }

    // The provider's own answer outranks a third-party catalog's description of it.
    [Fact]
    public void WhatTheProviderAlreadyStatedIsNeverOverwritten()
    {
        var discovered = Discovered("gpt-4o") with { InputCostPer1MUsd = 9.99m, MaxContextTokens = 4_096 };

        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(discovered),
            [Catalog("openai", "gpt-4o", input: 2.5m, output: 10m, context: 128_000)]);

        var model = Assert.Single(result.Models);
        Assert.Equal(9.99m, model.InputCostPer1MUsd);
        Assert.Equal(4_096, model.MaxContextTokens);
        // The gap it did not fill is still filled.
        Assert.Equal(10m, model.OutputCostPer1MUsd);
    }

    [Fact]
    public void AModelTheCatalogHasNeverHeardOfIsLeftExactlyAsDiscovered()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("private-finetune-7b")),
            [Catalog("openai", "gpt-4o", input: 2.5m, output: 10m)]);

        var model = Assert.Single(result.Models);
        Assert.Null(model.InputCostPer1MUsd);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AnEmptyCatalogChangesNothing()
    {
        var discovery = Discovery(Discovered("gpt-4o"));

        Assert.Same(discovery, DiscoveredModelCatalogEnricher.Enrich(discovery, []));
    }

    // The case that made a negotiated rate pointless: an operator prices a model their tenant buys through a
    // gateway, the snapshot still lists the same id under the upstream vendors at list price, and treating the
    // operator's rate as one more conflicting candidate left the model unpriced. Their rate is the answer.
    [Fact]
    public void ATenantsNegotiatedRateSettlesWhatTheSnapshotDisagreesAbout()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("gpt-5.6-luna")),
            [
                Catalog("openai", "gpt-5.6-luna", input: 0.2m, output: 1.2m, layer: AiModelCatalogLayer.TenantOverride),
                Catalog("openai", "gpt-5.6-luna", input: 1m, output: 6m),
                Catalog("azure", "gpt-5.6-luna", input: 1m, output: 6m),
            ]);

        var model = Assert.Single(result.Models);
        Assert.Equal(0.2m, model.InputCostPer1MUsd);
        Assert.Equal(1.2m, model.OutputCostPer1MUsd);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("left unpriced", StringComparison.Ordinal));
    }

    // A client's own rate is narrower than its tenant's, so it wins where both exist.
    [Fact]
    public void AClientsOwnRateOutranksItsTenants()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("gpt-5.6-luna")),
            [
                Catalog("openai", "gpt-5.6-luna", input: 0.2m, output: 1.2m, layer: AiModelCatalogLayer.TenantOverride),
                Catalog("openai", "gpt-5.6-luna", input: 0.1m, output: 0.6m, layer: AiModelCatalogLayer.ClientOverride),
            ]);

        Assert.Equal(0.1m, Assert.Single(result.Models).InputCostPer1MUsd);
    }

    // Narrowing to the operator's layer is not a licence to guess within it: two overrides naming different
    // providers at different rates are as ambiguous as two snapshot providers would be.
    [Fact]
    public void TwoOverridesAtDifferentRatesAreStillAmbiguous()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("gpt-5.6-luna")),
            [
                Catalog("openai", "gpt-5.6-luna", input: 0.2m, output: 1.2m, layer: AiModelCatalogLayer.TenantOverride),
                Catalog("azure", "gpt-5.6-luna", input: 0.9m, output: 4m, layer: AiModelCatalogLayer.TenantOverride),
            ]);

        Assert.Null(Assert.Single(result.Models).InputCostPer1MUsd);
        Assert.Contains(result.Warnings, warning => warning.Contains("left unpriced", StringComparison.Ordinal));
    }

    // Where the rate came from is reported, because a price is only right if it came from the right place.
    [Fact]
    public void ANegotiatedRateIsReportedAsSuch()
    {
        var result = DiscoveredModelCatalogEnricher.Enrich(
            Discovery(Discovered("gpt-5.6-luna")),
            [Catalog("openai", "gpt-5.6-luna", input: 0.2m, output: 1.2m, layer: AiModelCatalogLayer.TenantOverride)]);

        Assert.Contains(result.Warnings, warning => warning.Contains("negotiated rate", StringComparison.Ordinal));
    }

    private static AiModelDiscoveryResultDto Discovery(params AiConfiguredModelDto[] models)
    {
        return new AiModelDiscoveryResultDto("succeeded", true, [], models);
    }

    private static AiConfiguredModelDto Discovered(string remoteModelId)
    {
        return new AiConfiguredModelDto(
            Guid.NewGuid(),
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.ChatCompletions]);
    }

    private static AiModelCatalogEntryDto Catalog(
        string providerId,
        string remoteModelId,
        decimal? input = null,
        decimal? output = null,
        int? context = null,
        AiModelCatalogLayer layer = AiModelCatalogLayer.Global)
    {
        return new AiModelCatalogEntryDto(
            providerId,
            providerId,
            remoteModelId,
            remoteModelId,
            null,
            true,
            true,
            true,
            false,
            null,
            context,
            null,
            input,
            output,
            null,
            null,
            false,
            null,
            layer);
    }
}
