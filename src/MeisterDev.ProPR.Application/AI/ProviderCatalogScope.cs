// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Which entries of the model catalog describe the models a provider family serves.
/// </summary>
/// <remarks>
///     A vendor family serves the models of one catalog provider, so its catalog entries are those of that provider.
///     An OpenAI-compatible endpoint or a LiteLLM gateway can front a model of any vendor, so every catalog entry can
///     describe one of its models. A family that is not in this map, such as one loaded from an add-in directory, has
///     no catalog provider of its own.
/// </remarks>
public static class ProviderCatalogScope
{
    private static readonly FrozenDictionary<string, string> CatalogProviderByKind =
        new Dictionary<string, string>(ProviderVocabulary.KeyComparer)
        {
            ["meisterdev/openAi"] = "openai",
            ["meisterdev/azureOpenAi"] = "azure",
            ["meisterdev/anthropic"] = "anthropic",
            ["meisterdev/googleVertex"] = "google-vertex",
            ["meisterdev/awsBedrock"] = "amazon-bedrock",
        }.ToFrozenDictionary(ProviderVocabulary.KeyComparer);

    private static readonly FrozenSet<string> WholeCatalogKinds =
        new[] { "meisterdev/openAiCompatible", "meisterdev/liteLlm" }.ToFrozenSet(ProviderVocabulary.KeyComparer);

    /// <summary>The catalog provider whose entries describe the family's models, or null for a family without one.</summary>
    /// <param name="providerKind">The declared key of the provider family.</param>
    public static string? CatalogProviderOf(string? providerKind)
    {
        return providerKind is not null && CatalogProviderByKind.TryGetValue(providerKind, out var providerId)
            ? providerId
            : null;
    }

    /// <summary>Whether every catalog entry can describe a model of the family.</summary>
    /// <param name="providerKind">The declared key of the provider family.</param>
    public static bool ServesWholeCatalog(string? providerKind)
    {
        return providerKind is not null && WholeCatalogKinds.Contains(providerKind);
    }

    /// <summary>
    ///     The catalog entries that can describe a model of the family: the entries of its own catalog provider, every
    ///     entry for a family that serves the whole catalog, and none for any other family.
    /// </summary>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="catalog">The effective catalog of the client or tenant.</param>
    public static IReadOnlyList<AiModelCatalogEntryDto> EntriesFor(
        string? providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (ServesWholeCatalog(providerKind))
        {
            return catalog;
        }

        return CatalogProviderOf(providerKind) is { } providerId
            ? catalog.Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
    }

    /// <summary>
    ///     The entries of the family's own catalog provider among <paramref name="candidates" />, or all candidates when
    ///     none of them is from that provider.
    /// </summary>
    /// <remarks>
    ///     A model identifier such as <c>gpt-4o</c> is listed by several catalog providers at different prices. An
    ///     OpenAI connection buys it from OpenAI, so the OpenAI entry is the one that applies to it.
    /// </remarks>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="candidates">Catalog entries with the same model identifier.</param>
    public static List<AiModelCatalogEntryDto> PreferOwnProvider(
        string? providerKind,
        IEnumerable<AiModelCatalogEntryDto> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var materialized = candidates as List<AiModelCatalogEntryDto> ?? [.. candidates];
        if (CatalogProviderOf(providerKind) is not { } providerId)
        {
            return materialized;
        }

        var own = materialized
            .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return own.Count > 0 ? own : materialized;
    }
}
