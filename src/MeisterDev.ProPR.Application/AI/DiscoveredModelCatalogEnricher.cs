// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Fills in what a provider's discovery response cannot tell us: price, context window and capabilities.
/// </summary>
/// <remarks>
///     <para>
///         A model-list endpoint returns identifiers, not economics — several return nothing but an id — so a
///         discovered model arrives with no cost at all and a budget cap enforced against it would be enforced
///         against zero. The catalog knows those facts, and matching the two up is the only way discovery
///         produces a model that is ready to use rather than one an operator must go and price by hand.
///     </para>
///     <para>
///         It never guesses. A model id offered by more than one catalog provider at different prices is left
///         alone and reported, because picking one would silently bill a gateway's traffic at the underlying
///         vendor's rate — the browse-and-pick surface exists for exactly that case, and it knows which provider
///         the operator meant.
///     </para>
///     <para>
///         An operator's own rate is exempt from that, because it is not another opinion to weigh against the
///         catalog's: it is the answer to which rate applies. When a client or tenant override is present for a
///         model, the layers beneath it are discarded before ambiguity is considered at all. Treating a negotiated
///         rate as merely one more conflicting candidate left the model unpriced, which made recording the rate
///         pointless in exactly the case it was entered for.
///     </para>
/// </remarks>
public static class DiscoveredModelCatalogEnricher
{
    /// <summary>
    ///     Returns the discovery result with catalog facts merged into its models, plus a warning for anything
    ///     that could not be priced unambiguously.
    /// </summary>
    /// <param name="discovered">The provider's discovery result.</param>
    /// <param name="catalog">Catalog entries the client may draw on, overrides already applied.</param>
    public static AiModelDiscoveryResultDto Enrich(
        AiModelDiscoveryResultDto discovered,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(catalog);

        if (discovered.Models.Count == 0 || catalog.Count == 0)
        {
            return discovered;
        }

        var byModelId = catalog
            .GroupBy(entry => entry.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var models = new List<AiConfiguredModelDto>(discovered.Models.Count);
        var priced = new List<string>();
        var ambiguous = new List<string>();

        foreach (var model in discovered.Models)
        {
            if (!byModelId.TryGetValue(model.RemoteModelId, out var offered))
            {
                models.Add(model);
                continue;
            }

            var candidates = ModelCatalogLayerPrecedence.NarrowToMostSpecific(offered);
            var distinct = candidates.DistinctBy(Fingerprint).ToList();
            if (distinct.Count > 1)
            {
                ambiguous.Add($"{model.RemoteModelId} ({string.Join(", ", candidates.Select(entry => entry.ProviderId).Distinct().Order())})");
                models.Add(model);
                continue;
            }

            models.Add(Merge(model, candidates[0]));
            priced.Add(Describe(model.RemoteModelId, candidates[0]));
        }

        var warnings = discovered.Warnings.ToList();
        if (priced.Count > 0)
        {
            // Naming the source provider matters: a price is only right if it came from the provider the models
            // are actually being bought through.
            warnings.Add($"Priced from the catalog: {string.Join("; ", priced)}.");
        }

        if (ambiguous.Count > 0)
        {
            warnings.Add(
                "Several catalog providers offer these models at different rates, so they were left unpriced — "
                + $"pick them from the catalog to price them exactly: {string.Join("; ", ambiguous)}.");
        }

        return discovered with { Models = models, Warnings = warnings };
    }

    /// <summary>Names where a price came from, since a rate is only right if it came from the right place.</summary>
    private static string Describe(string remoteModelId, AiModelCatalogEntryDto entry)
    {
        return entry.PricingLayer switch
        {
            AiModelCatalogLayer.ClientOverride => $"{remoteModelId} at this client's own rate for '{entry.ProviderId}'",
            AiModelCatalogLayer.TenantOverride => $"{remoteModelId} at the tenant's negotiated rate for '{entry.ProviderId}'",
            _ => $"{remoteModelId} from '{entry.ProviderId}'",
        };
    }

    // Two catalog entries are interchangeable for this purpose when they agree on everything being copied over.
    private static string Fingerprint(AiModelCatalogEntryDto entry)
    {
        return string.Join(
            '|',
            entry.InputCostPer1MUsd,
            entry.OutputCostPer1MUsd,
            entry.CachedInputCostPer1MUsd,
            entry.CacheWriteCostPer1MUsd,
            entry.MaxContextTokens,
            entry.SupportsToolUse,
            entry.SupportsStructuredOutput,
            entry.SupportsReasoning,
            entry.SupportsPromptCaching,
            entry.ReasoningContentField);
    }

    // Only absent values are filled. Whatever the provider itself stated about a model outranks a third-party
    // catalog's description of it, and an operator's own entry outranks both.
    private static AiConfiguredModelDto Merge(AiConfiguredModelDto model, AiModelCatalogEntryDto entry)
    {
        return model with
        {
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) || model.DisplayName == model.RemoteModelId
                ? entry.DisplayName ?? model.DisplayName
                : model.DisplayName,
            MaxContextTokens = model.MaxContextTokens ?? entry.MaxContextTokens,
            InputCostPer1MUsd = model.InputCostPer1MUsd ?? entry.InputCostPer1MUsd,
            OutputCostPer1MUsd = model.OutputCostPer1MUsd ?? entry.OutputCostPer1MUsd,
            CachedInputCostPer1MUsd = model.CachedInputCostPer1MUsd ?? entry.CachedInputCostPer1MUsd,
            CacheWriteCostPer1MUsd = model.CacheWriteCostPer1MUsd ?? entry.CacheWriteCostPer1MUsd,
            SupportsStructuredOutput = model.SupportsStructuredOutput || entry.SupportsStructuredOutput,
            SupportsToolUse = model.SupportsToolUse || entry.SupportsToolUse,
            SupportsReasoning = model.SupportsReasoning || entry.SupportsReasoning,
            SupportsPromptCaching = model.SupportsPromptCaching || entry.SupportsPromptCaching,
            ReasoningContentField = model.ReasoningContentField ?? entry.ReasoningContentField,
        };
    }
}
