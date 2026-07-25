// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A tenant's override of catalog values for one model. Only pricing and the display name are overridable,
///     because those are what can genuinely differ for a customer; a model's capabilities are facts about the
///     model and always come from the snapshot.
/// </summary>
/// <param name="ProviderId">Catalog provider identifier the override applies to.</param>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Replacement display name, or null to keep the snapshot's.</param>
/// <param name="InputCostPer1MUsd">Negotiated USD per million input tokens, or null to inherit.</param>
/// <param name="OutputCostPer1MUsd">Negotiated USD per million output tokens, or null to inherit.</param>
/// <param name="CachedInputCostPer1MUsd">Negotiated USD per million cache-read tokens, or null to inherit.</param>
/// <param name="CacheWriteCostPer1MUsd">Negotiated USD per million cache-write tokens, or null to inherit.</param>
public sealed record AiModelCatalogOverrideDto(
    string ProviderId,
    string RemoteModelId,
    string? DisplayName = null,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null,
    decimal? CachedInputCostPer1MUsd = null,
    decimal? CacheWriteCostPer1MUsd = null);
