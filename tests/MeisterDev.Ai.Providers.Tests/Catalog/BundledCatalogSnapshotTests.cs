// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;

namespace MeisterDev.Ai.Providers.Tests.Catalog;

/// <summary>
///     Guards the bundled snapshot itself. An embedded resource is easy to drop from a build without anything
///     failing loudly, and a snapshot that silently stops importing would leave a fresh installation with an
///     empty catalog and no obvious cause.
/// </summary>
public sealed class BundledCatalogSnapshotTests
{
    private static async Task<IReadOnlyList<ProviderCatalogEntry>> Bundled()
    {
        await using var stream = BundledCatalogSnapshot.Open();
        return await new ModelsDevCatalogSnapshotImporter().ImportAsync(stream);
    }

    [Fact]
    public async Task IsPresentAndImportsAMeaningfulNumberOfModels()
    {
        var entries = await Bundled();

        // A deliberately loose floor: it catches an empty or truncated resource without breaking every time
        // the snapshot is refreshed.
        Assert.True(entries.Count > 200, $"expected a populated snapshot, imported {entries.Count} entries");
    }

    [Fact]
    public void DeclaresTheFormatItsImporterUnderstands()
    {
        Assert.Equal(new ModelsDevCatalogSnapshotImporter().SourceFormat, BundledCatalogSnapshot.SourceFormat);
    }

    // The providers this product can actually reach. If a refresh trimmed one away, model selection for it
    // would quietly fall back to hand-entry.
    [Theory]
    [InlineData("openai")]
    [InlineData("azure")]
    [InlineData("anthropic")]
    [InlineData("google")]
    [InlineData("amazon-bedrock")]
    [InlineData("deepseek")]
    [InlineData("alibaba")]
    [InlineData("moonshotai")]
    [InlineData("openrouter")]
    public async Task CoversTheProvidersThisProductCanReach(string providerId)
    {
        Assert.Contains(providerId, (await Bundled()).Select(entry => entry.ProviderId).Distinct());
    }

    [Fact]
    public async Task CarriesTheReasoningContentQuirkForAtLeastOneModel()
    {
        // The quirk is the reason per-model normalization can be data-driven instead of hard-coded, so the
        // snapshot losing it would quietly remove that capability.
        Assert.Contains(await Bundled(), entry => entry.ReasoningContentField is not null);
    }

    [Fact]
    public async Task CarriesPricingForTheModelsItDescribes()
    {
        var entries = await Bundled();
        var priced = entries.Count(entry => entry.InputCostPer1MUsd is not null && entry.OutputCostPer1MUsd is not null);

        // Pricing is what makes provider-agnostic cost caps meaningful, so most entries must carry it.
        Assert.True(priced > entries.Count / 2, $"only {priced} of {entries.Count} entries carry pricing");
    }
}
