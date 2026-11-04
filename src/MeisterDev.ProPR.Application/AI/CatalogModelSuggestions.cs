// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     The catalog models a discovery offers beside the models the provider listed, so an operator can pick a model
///     when the provider lists none or not the one needed.
/// </summary>
/// <remarks>
///     <para>
///         The entries come from <see cref="ProviderCatalogScope.EntriesFor" />. A model the provider listed is left
///         out, and so is an image, video, speech, transcription, realtime or moderation model, recognised by its
///         identifier or family, because neither a review nor an embedding call can use one. The catalog holds no
///         operation for a model, so an entry whose identifier or family contains <c>embed</c> is offered as an
///         embedding model with the values of <see cref="EmbeddingModelDefaults" />, and every other entry as a chat
///         model.
///     </para>
///     <para>
///         Entries with the same identifier from several catalog providers are offered once. When they differ in
///         price, the offered model carries no price, because the catalog does not record which provider the
///         endpoint buys the model from.
///     </para>
///     <para>
///         The list holds at most <see cref="MaximumModels" /> entries: the embedding models first, then the chat
///         models with the newest release date first, each group ordered by identifier where the dates are equal.
///     </para>
/// </remarks>
public static class CatalogModelSuggestions
{
    /// <summary>The most catalog models one discovery offers.</summary>
    public const int MaximumModels = 200;

    private static readonly string[] UnservedKinds =
        ["image", "video", "realtime", "audio", "tts", "transcribe", "whisper", "dall-e", "sora", "moderation"];

    /// <summary>Lists the catalog models offered for the family.</summary>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="catalog">The effective catalog of the client; empty when there is none.</param>
    /// <param name="listedModelIds">The identifiers the provider listed.</param>
    /// <param name="chatProtocolModes">The protocol modes a chat model of the family is offered with.</param>
    public static IReadOnlyList<AiConfiguredModelDto> Suggest(
        string providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog,
        IEnumerable<string> listedModelIds,
        IReadOnlyList<string> chatProtocolModes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(listedModelIds);
        ArgumentNullException.ThrowIfNull(chatProtocolModes);

        var listed = new HashSet<string>(
            listedModelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return ProviderCatalogScope.EntriesFor(providerKind, catalog)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RemoteModelId))
            .Where(entry => !listed.Contains(entry.RemoteModelId.Trim()))
            .Where(IsServable)
            .GroupBy(entry => entry.RemoteModelId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => Collapse(group.ToList()))
            .Select(entry => (Entry: entry, Embedding: IsEmbedding(entry)))
            .OrderBy(candidate => candidate.Embedding ? 0 : 1)
            .ThenByDescending(candidate => candidate.Entry.ReleaseDate ?? DateOnly.MinValue)
            .ThenBy(candidate => candidate.Entry.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumModels)
            .Select(candidate => Describe(candidate.Entry, candidate.Embedding, providerKind, catalog, chatProtocolModes))
            .ToList();
    }

    /// <summary>Whether the catalog entry describes an embedding model.</summary>
    /// <param name="entry">The catalog entry.</param>
    public static bool IsEmbedding(AiModelCatalogEntryDto entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Names(entry, "embed");
    }

    private static bool IsServable(AiModelCatalogEntryDto entry)
    {
        return !Array.Exists(UnservedKinds, kind => Names(entry, kind));
    }

    private static bool Names(AiModelCatalogEntryDto entry, string fragment)
    {
        return entry.RemoteModelId.Contains(fragment, StringComparison.OrdinalIgnoreCase)
               || (entry.Family?.Contains(fragment, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // The narrowest scope layer speaks for the model, as it does when a discovered model is priced. The first
    // entry by provider supplies the description, and the price is dropped when the remaining entries disagree on it.
    private static AiModelCatalogEntryDto Collapse(List<AiModelCatalogEntryDto> group)
    {
        var candidates = ModelCatalogLayerPrecedence.NarrowToMostSpecific(group)
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ToList();
        var chosen = candidates[0];
        var priced = candidates
            .Select(entry => (entry.InputCostPer1MUsd, entry.OutputCostPer1MUsd, entry.CachedInputCostPer1MUsd, entry.CacheWriteCostPer1MUsd))
            .Distinct()
            .Count() == 1;

        return priced
            ? chosen
            : chosen with
            {
                InputCostPer1MUsd = null,
                OutputCostPer1MUsd = null,
                CachedInputCostPer1MUsd = null,
                CacheWriteCostPer1MUsd = null,
            };
    }

    private static AiConfiguredModelDto Describe(
        AiModelCatalogEntryDto entry,
        bool embedding,
        string providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog,
        IReadOnlyList<string> chatProtocolModes)
    {
        var remoteModelId = entry.RemoteModelId.Trim();
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? remoteModelId : entry.DisplayName;

        if (embedding)
        {
            var model = new AiConfiguredModelDto(
                Guid.Empty,
                remoteModelId,
                displayName,
                [AiOperationKind.Embedding],
                [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                Source: AiConfiguredModelSource.KnownCatalog,
                InputCostPer1MUsd: entry.InputCostPer1MUsd,
                OutputCostPer1MUsd: entry.OutputCostPer1MUsd,
                MaxContextTokens: entry.MaxContextTokens);

            return EmbeddingModelDefaults.Apply(model, providerKind, catalog);
        }

        return new AiConfiguredModelDto(
            Guid.Empty,
            remoteModelId,
            displayName,
            [AiOperationKind.Chat],
            chatProtocolModes,
            SupportsStructuredOutput: entry.SupportsStructuredOutput,
            SupportsToolUse: entry.SupportsToolUse,
            Source: AiConfiguredModelSource.KnownCatalog,
            InputCostPer1MUsd: entry.InputCostPer1MUsd,
            OutputCostPer1MUsd: entry.OutputCostPer1MUsd,
            MaxContextTokens: entry.MaxContextTokens,
            CachedInputCostPer1MUsd: entry.CachedInputCostPer1MUsd,
            CacheWriteCostPer1MUsd: entry.CacheWriteCostPer1MUsd,
            SupportsReasoning: entry.SupportsReasoning,
            SupportsPromptCaching: entry.SupportsPromptCaching,
            ReasoningContentField: entry.ReasoningContentField);
    }
}
