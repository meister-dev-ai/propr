// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Decides which scope layer speaks for a model when several offer the same model id.
/// </summary>
/// <remarks>
///     An operator's own rate is not one opinion among several to be weighed against a third-party snapshot: it
///     is the statement of what the model actually costs this customer. Wherever a client or tenant entry exists,
///     the layers beneath it are discarded before anything else is decided, so a negotiated rate settles the
///     question instead of creating a conflict that leaves the model unpriced.
/// </remarks>
public static class ModelCatalogLayerPrecedence
{
    /// <summary>
    ///     Keeps only the entries from the narrowest scope layer present among <paramref name="candidates" />.
    /// </summary>
    /// <remarks>
    ///     Ambiguity within the surviving layer is still ambiguity, and is left for the caller to judge: two
    ///     tenant overrides naming different providers at different rates are no more decidable than two snapshot
    ///     providers would be.
    /// </remarks>
    public static List<AiModelCatalogEntryDto> NarrowToMostSpecific(IEnumerable<AiModelCatalogEntryDto> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var materialized = candidates as List<AiModelCatalogEntryDto> ?? [.. candidates];
        if (materialized.Count == 0)
        {
            return materialized;
        }

        var mostSpecific = materialized.Max(entry => entry.PricingLayer);
        return mostSpecific == AiModelCatalogLayer.Global
            ? materialized
            : materialized.Where(entry => entry.PricingLayer == mostSpecific).ToList();
    }
}
