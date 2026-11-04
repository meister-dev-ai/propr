// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     The tokenizer, input limit and vector width ProPR assumes for an embedding model whose configuration leaves
///     them out, and the values it accepts when they are stated.
/// </summary>
/// <remarks>
///     <para>
///         A missing value is filled from three sources in order. The first is the table of embedding models whose
///         values the vendor documents, <see cref="KnownEmbeddingModels" />. The second is the context window of the
///         model in the catalog, which fills the input limit when the catalog entries for the model agree on one.
///         The third is <see cref="FallbackTokenizerName" />, <see cref="FallbackMaxInputTokens" /> and
///         <see cref="VectorDimensions" />. A stated value is kept.
///     </para>
///     <para>
///         ProPR stores every embedding vector in a column of <see cref="VectorDimensions" /> dimensions. A width
///         taken from the table can differ from it; the purpose selection refuses such a model, and the model check
///         reports the width the model returns.
///     </para>
/// </remarks>
public static class EmbeddingModelDefaults
{
    /// <summary>
    ///     The vector width of every embedding column. An embedding purpose accepts a model configured for this width
    ///     and refuses any other.
    /// </summary>
    public const int VectorDimensions = 1536;

    /// <summary>The tokenizer assumed for an embedding model that is not in the table.</summary>
    public const string FallbackTokenizerName = "cl100k_base";

    /// <summary>The input limit assumed for an embedding model that is not in the table and not in the catalog.</summary>
    public const int FallbackMaxInputTokens = 8191;

    /// <summary>The smallest vector width a configured embedding model may state.</summary>
    public const int MinimumDimensions = 64;

    /// <summary>The largest vector width a configured embedding model may state.</summary>
    public const int MaximumDimensions = 4096;

    /// <summary>
    ///     The tokenizers an embedding model may state, which the embedding paths count input tokens with. The
    ///     <c>claude</c> encoding of the tokenizer registry describes Anthropic chat models and no embedding model, so
    ///     an embedding model may not state it.
    /// </summary>
    public static IReadOnlyList<string> SupportedTokenizerNames { get; } =
    [
        "cl100k_base",
        "o200k_base",
        "o200k_harmony",
        "r50k_base",
        "p50k_base",
        "p50k_edit",
    ];

    /// <summary>Whether an embedding model may state the tokenizer.</summary>
    /// <param name="tokenizerName">The stated tokenizer.</param>
    public static bool IsSupportedTokenizer(string? tokenizerName)
    {
        var trimmed = tokenizerName?.Trim();
        return !string.IsNullOrEmpty(trimmed)
               && SupportedTokenizerNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Why an embedding model may not state the tokenizer, or null when it may. An empty tokenizer is accepted,
    ///     because the save fills it in.
    /// </summary>
    /// <param name="tokenizerName">The stated tokenizer.</param>
    /// <param name="remoteModelId">The model identifier, for the message.</param>
    public static string? TokenizerRefusal(string? tokenizerName, string remoteModelId)
    {
        return string.IsNullOrWhiteSpace(tokenizerName) || IsSupportedTokenizer(tokenizerName)
            ? null
            : $"Embedding model '{remoteModelId}' states a tokenizer that ProPR does not support for embedding models. "
              + $"Use one of {string.Join(", ", SupportedTokenizerNames)}, or leave the tokenizer empty.";
    }

    /// <summary>Whether a stated vector width is within the range a configured model may state.</summary>
    /// <param name="dimensions">The stated width.</param>
    public static bool IsAcceptedDimensions(int dimensions)
    {
        return dimensions is >= MinimumDimensions and <= MaximumDimensions;
    }

    /// <summary>Fills the missing tokenizer, input limit and vector width of an embedding model.</summary>
    /// <param name="model">The model. A model that serves no embedding operation is returned unchanged.</param>
    /// <param name="providerKind">The declared key of the family that serves the model.</param>
    /// <param name="catalog">The effective catalog of the client or tenant; empty when there is none.</param>
    public static AiConfiguredModelDto Apply(
        AiConfiguredModelDto model,
        string? providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!model.SupportsEmbedding || !IsMissingAny(model))
        {
            return model;
        }

        KnownEmbeddingModels.TryFind(model.RemoteModelId, out var known);
        return model with
        {
            TokenizerName = string.IsNullOrWhiteSpace(model.TokenizerName)
                ? known?.TokenizerName ?? FallbackTokenizerName
                : model.TokenizerName.Trim(),
            MaxInputTokens = model.MaxInputTokens is > 0
                ? model.MaxInputTokens
                : known?.MaxInputTokens
                  ?? CatalogInputLimit(model.RemoteModelId, providerKind, catalog)
                  ?? FallbackMaxInputTokens,
            EmbeddingDimensions = model.EmbeddingDimensions ?? known?.Dimensions ?? VectorDimensions,
        };
    }

    /// <summary>
    ///     Whether filling the model's missing values reads the catalog: the model serves embeddings, its input limit
    ///     is missing and the table has no entry for it.
    /// </summary>
    /// <param name="model">The model.</param>
    public static bool ReadsCatalog(AiConfiguredModelDto model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.SupportsEmbedding
               && model.MaxInputTokens is not > 0
               && !KnownEmbeddingModels.TryFind(model.RemoteModelId, out _);
    }

    /// <summary>
    ///     The context window the catalog states for the model, when its entries agree on one value.
    /// </summary>
    /// <remarks>
    ///     The identifier is matched as stated and, when no entry has it, without a leading <c>vendor/</c> path. The
    ///     entries of the family's own catalog provider are preferred over the others.
    /// </remarks>
    /// <param name="remoteModelId">The model identifier.</param>
    /// <param name="providerKind">The declared key of the family that serves the model.</param>
    /// <param name="catalog">The effective catalog of the client or tenant.</param>
    public static int? CatalogInputLimit(
        string remoteModelId,
        string? providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(remoteModelId);
        ArgumentNullException.ThrowIfNull(catalog);

        var id = remoteModelId.Trim();
        var matches = Matching(catalog, id);
        var unprefixed = KnownEmbeddingModels.WithoutVendorPrefix(id);
        if (matches.Count == 0 && unprefixed.Length != id.Length)
        {
            matches = Matching(catalog, unprefixed);
        }

        var limits = ProviderCatalogScope.PreferOwnProvider(providerKind, matches)
            .Select(entry => entry.MaxContextTokens)
            .Where(limit => limit is > 0)
            .Distinct()
            .ToList();

        return limits.Count == 1 ? limits[0] : null;
    }

    private static bool IsMissingAny(AiConfiguredModelDto model)
    {
        return string.IsNullOrWhiteSpace(model.TokenizerName)
               || model.MaxInputTokens is not > 0
               || model.EmbeddingDimensions is null;
    }

    private static List<AiModelCatalogEntryDto> Matching(IReadOnlyList<AiModelCatalogEntryDto> catalog, string remoteModelId)
    {
        return catalog
            .Where(entry => string.Equals(entry.RemoteModelId?.Trim(), remoteModelId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
