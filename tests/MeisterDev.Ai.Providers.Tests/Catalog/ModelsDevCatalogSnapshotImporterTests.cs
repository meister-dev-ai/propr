// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.Ai.Providers.Catalog;

namespace MeisterDev.Ai.Providers.Tests.Catalog;

public sealed class ModelsDevCatalogSnapshotImporterTests
{
    private static readonly ModelsDevCatalogSnapshotImporter Importer = new();

    [Fact]
    public async Task ReadsTheFactsTheSourceStates()
    {
        var entry = Assert.Single(
            await Import(
                """
                {
                  "deepseek": {
                    "id": "deepseek",
                    "name": "DeepSeek",
                    "models": {
                      "deepseek-reasoner": {
                        "id": "deepseek-reasoner",
                        "name": "DeepSeek Reasoner",
                        "family": "deepseek",
                        "reasoning": true,
                        "tool_call": true,
                        "structured_output": true,
                        "open_weights": true,
                        "release_date": "2026-01-20",
                        "interleaved": { "field": "reasoning_content" },
                        "limit": { "context": 131072, "output": 65536 },
                        "cost": { "input": 0.28, "output": 0.42, "cache_read": 0.028, "cache_write": 0.14 }
                      }
                    }
                  }
                }
                """));

        Assert.Equal("deepseek", entry.ProviderId);
        Assert.Equal("DeepSeek", entry.ProviderName);
        Assert.Equal("deepseek-reasoner", entry.RemoteModelId);
        Assert.Equal("DeepSeek Reasoner", entry.DisplayName);
        Assert.Equal("deepseek", entry.Family);
        Assert.True(entry.SupportsReasoning);
        Assert.True(entry.SupportsToolUse);
        Assert.True(entry.SupportsStructuredOutput);
        Assert.True(entry.OpenWeights);
        Assert.Equal(new DateOnly(2026, 1, 20), entry.ReleaseDate);
        Assert.Equal(131072, entry.MaxContextTokens);
        Assert.Equal(65536, entry.MaxOutputTokens);
        // Costs in this source are already per million USD, so they must survive unconverted and unrounded.
        Assert.Equal(0.28m, entry.InputCostPer1MUsd);
        Assert.Equal(0.42m, entry.OutputCostPer1MUsd);
        Assert.Equal(0.028m, entry.CachedInputCostPer1MUsd);
        Assert.Equal(0.14m, entry.CacheWriteCostPer1MUsd);
    }

    // The quirk a normalizing stage acts on: a model that interleaves reasoning names the field it needs
    // echoed back, and a model that does not carries nothing.
    [Fact]
    public async Task ReasoningContentField_IsCarriedOnlyForModelsThatDeclareIt()
    {
        var entries = await Import(
            """
            {
              "p": { "id": "p", "name": "P", "models": {
                "quirky": { "id": "quirky", "name": "Quirky", "reasoning": true,
                            "interleaved": { "field": "reasoning_content" },
                            "limit": { "context": 1, "output": 1 } },
                "plain":  { "id": "plain",  "name": "Plain",  "reasoning": true,
                            "limit": { "context": 1, "output": 1 } }
              } }
            }
            """);

        Assert.Equal("reasoning_content", entries.Single(e => e.RemoteModelId == "quirky").ReasoningContentField);
        Assert.Null(entries.Single(e => e.RemoteModelId == "plain").ReasoningContentField);
    }

    // There is no "supports caching" flag in the source, so a stated cache price is the signal. A zero price is
    // still a statement that the path exists, which is why it counts.
    [Theory]
    [InlineData("""{ "input": 1, "output": 2 }""", false)]
    [InlineData("""{ "input": 1, "output": 2, "cache_read": 0.1 }""", true)]
    [InlineData("""{ "input": 1, "output": 2, "cache_write": 0 }""", true)]
    public async Task PromptCachingSupport_IsInferredFromAStatedCachePrice(string cost, bool expected)
    {
        var entry = Assert.Single(
            await Import(
                $$"""
                  { "p": { "id": "p", "name": "P", "models": {
                      "m": { "id": "m", "name": "M", "limit": { "context": 1, "output": 1 }, "cost": {{cost}} } } } }
                  """));

        Assert.Equal(expected, entry.SupportsPromptCaching);
    }

    // A snapshot is third-party data that may gain models or fields this build has never seen, so a single
    // unusable entry must not cost us the whole import.
    [Fact]
    public async Task MalformedEntries_AreSkippedRatherThanFailingTheImport()
    {
        var entries = await Import(
            """
            {
              "good": { "id": "good", "name": "Good", "models": {
                  "m": { "id": "m", "name": "M", "limit": { "context": 8, "output": 4 } } } },
              "no-models": { "id": "no-models", "name": "No Models" },
              "models-not-an-object": { "id": "x", "name": "X", "models": 42 },
              "model-not-an-object": { "id": "y", "name": "Y", "models": { "m": "nope" } },
              "unknown-field": { "id": "z", "name": "Z", "models": {
                  "m2": { "id": "m2", "name": "M2", "limit": { "context": 1, "output": 1 },
                          "something_new": { "nested": true } } } }
            }
            """);

        Assert.Equal(["m", "m2"], entries.Select(e => e.RemoteModelId).OrderBy(id => id));
    }

    [Fact]
    public async Task AbsentOptionalFields_YieldNullsRatherThanZeroes()
    {
        // A missing price is unknown, not free; conflating the two would silently under-bill a cap.
        var entry = Assert.Single(
            await Import(
                """
                { "p": { "id": "p", "name": "P", "models": {
                    "m": { "id": "m", "name": "M", "limit": { "context": 1, "output": 1 } } } } }
                """));

        Assert.Null(entry.InputCostPer1MUsd);
        Assert.Null(entry.OutputCostPer1MUsd);
        Assert.Null(entry.CachedInputCostPer1MUsd);
        Assert.Null(entry.CacheWriteCostPer1MUsd);
        Assert.Null(entry.Family);
        Assert.Null(entry.ReleaseDate);
        Assert.False(entry.SupportsPromptCaching);
    }

    [Fact]
    public async Task NonObjectRoot_YieldsNothing()
    {
        Assert.Empty(await Import("[]"));
    }

    private static async Task<IReadOnlyList<ProviderCatalogEntry>> Import(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await Importer.ImportAsync(stream);
    }
}
