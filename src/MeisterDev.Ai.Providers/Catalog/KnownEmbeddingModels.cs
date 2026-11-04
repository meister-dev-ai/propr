// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>What the vendor documents about one embedding model.</summary>
/// <param name="RemoteModelId">The identifier the vendor publishes the model under.</param>
/// <param name="TokenizerName">The encoding the model counts its input with.</param>
/// <param name="MaxInputTokens">The most tokens one input may hold.</param>
/// <param name="Dimensions">The length of the vector the model returns when no shorter one is requested.</param>
public sealed record KnownEmbeddingModel(string RemoteModelId, string TokenizerName, int MaxInputTokens, int Dimensions);

/// <summary>
///     The embedding models whose tokenizer, input limit and vector width are documented by their vendor.
/// </summary>
/// <remarks>
///     A model list carries identifiers, and the catalog snapshot carries no vector width, so these three values
///     cannot be read from either source. A model absent from this table has no entry here, and the caller decides
///     what to assume for it.
/// </remarks>
public static class KnownEmbeddingModels
{
    private static readonly FrozenDictionary<string, KnownEmbeddingModel> ByModel =
        new KnownEmbeddingModel[]
            {
                new("text-embedding-3-small", "cl100k_base", 8191, 1536),
                new("text-embedding-3-large", "cl100k_base", 8191, 3072),
                new("text-embedding-ada-002", "cl100k_base", 8191, 1536),
            }
            .ToFrozenDictionary(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The models in the table.</summary>
    public static IReadOnlyCollection<KnownEmbeddingModel> All => ByModel.Values;

    /// <summary>Finds the documented values of an embedding model.</summary>
    /// <remarks>
    ///     Gateways and routers publish a vendor model under the vendor's name followed by a slash, for example
    ///     <c>openai/text-embedding-3-small</c>, so the part after the last slash is matched when the whole identifier
    ///     is not in the table. The match ignores case.
    /// </remarks>
    /// <param name="remoteModelId">The model identifier as the endpoint or the operator states it.</param>
    /// <param name="model">The documented values, or null when the model is not in the table.</param>
    /// <returns>Whether the model is in the table.</returns>
    public static bool TryFind(string? remoteModelId, [NotNullWhen(true)] out KnownEmbeddingModel? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(remoteModelId))
        {
            return false;
        }

        var trimmed = remoteModelId.Trim();
        if (ByModel.TryGetValue(trimmed, out model))
        {
            return true;
        }

        var unprefixed = WithoutVendorPrefix(trimmed);
        return unprefixed.Length != trimmed.Length && ByModel.TryGetValue(unprefixed, out model);
    }

    /// <summary>The model identifier without a leading <c>vendor/</c> path, or the identifier itself without one.</summary>
    /// <param name="remoteModelId">The model identifier.</param>
    public static string WithoutVendorPrefix(string remoteModelId)
    {
        ArgumentNullException.ThrowIfNull(remoteModelId);

        var slash = remoteModelId.LastIndexOf('/');
        return slash < 0 || slash == remoteModelId.Length - 1 ? remoteModelId : remoteModelId[(slash + 1)..];
    }
}
