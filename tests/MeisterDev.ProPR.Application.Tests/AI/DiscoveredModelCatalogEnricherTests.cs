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
        int? context = null)
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
            AiModelCatalogLayer.Global);
    }
}
