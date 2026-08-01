// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     EF Core implementation of <see cref="IModelPricingResolver" />. Reads the configured models for an AI
///     connection from a short-lived <see cref="MeisterProPRDbContext" /> and resolves the pricing of the
///     model a review pass used, matching first by model id (remote id or display name) and then by the
///     purpose bound to the pass's effort tier. Returns <see langword="null" /> whenever the connection,
///     model, or a usable match cannot be found.
/// </summary>
/// <remarks>
///     <para>
///         A connection that states its own rate is believed outright: it is the narrowest thing an operator can
///         set, and it describes the endpoint the tokens were actually bought through.
///     </para>
///     <para>
///         When it states none, the catalog is read rather than treated as silence. An operator who recorded a
///         negotiated rate for the model has stated what it costs, and billing that recorded traffic as unpriced
///         made the rate they entered do nothing. The same layer precedence applies as everywhere else, and an
///         ambiguous answer is still no answer: several providers offering the model at different rates within
///         the surviving layer leaves it unpriced rather than billed at a guess.
///     </para>
/// </remarks>
public sealed class EfModelPricingResolver(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    TimeProvider timeProvider)
    : IModelPricingResolver
{
    /// <inheritdoc />
    public async Task<ModelPricing?> ResolveAsync(
        Guid connectionId,
        AiConnectionModelCategory category,
        string modelId,
        CancellationToken ct)
    {
        if (connectionId == Guid.Empty)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var models = await db.AiConfiguredModels
            .AsNoTracking()
            .Where(model => model.ConnectionProfileId == connectionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (models.Count == 0)
        {
            return null;
        }

        var match = string.IsNullOrEmpty(modelId)
            ? null
            : models.FirstOrDefault(model =>
                string.Equals(model.RemoteModelId, modelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.DisplayName, modelId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var purposeName = MapCategoryToPurpose(category).ToString();
            var binding = await db.AiPurposeBindings
                .AsNoTracking()
                .Where(candidate => candidate.ConnectionProfileId == connectionId
                                    && candidate.IsEnabled
                                    && candidate.Purpose == purposeName)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (binding is not null)
            {
                match = models.FirstOrDefault(model => model.Id == binding.ConfiguredModelId);
            }
        }

        if (match is null)
        {
            return null;
        }

        if (match.InputCostPer1MUsd is not null || match.OutputCostPer1MUsd is not null)
        {
            return new ModelPricing(
                match.InputCostPer1MUsd,
                match.OutputCostPer1MUsd,
                match.CachedInputCostPer1MUsd,
                match.CacheWriteCostPer1MUsd);
        }

        return await this.ResolveFromCatalogAsync(db, connectionId, match.RemoteModelId, ct).ConfigureAwait(false)
               ?? new ModelPricing(null, null);
    }

    /// <summary>
    ///     Reads the rate an operator recorded in the catalog for this model, as the client behind
    ///     <paramref name="connectionId" /> sees it.
    /// </summary>
    private async Task<ModelPricing?> ResolveFromCatalogAsync(
        MeisterProPRDbContext db,
        Guid connectionId,
        string remoteModelId,
        CancellationToken ct)
    {
        var scope = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == connectionId)
            .Select(profile => new { profile.ClientId, profile.TenantId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (scope is null)
        {
            return null;
        }

        var catalog = new ModelCatalogRepository(db, timeProvider);

        // A profile belongs to a client or to a tenant. Either resolves the same three layers; which one is
        // asked simply follows who owns the connection the tokens were bought through.
        var entries = scope.ClientId is { } clientId
            ? await catalog.GetEffectiveForClientAsync(clientId, ct: ct).ConfigureAwait(false)
            : scope.TenantId is { } tenantId
                ? await catalog.GetEffectiveForTenantAsync(tenantId, ct: ct).ConfigureAwait(false)
                : [];

        var candidates = ModelCatalogLayerPrecedence.NarrowToMostSpecific(
            entries.Where(entry => string.Equals(entry.RemoteModelId, remoteModelId, StringComparison.OrdinalIgnoreCase)));

        // Only the rates matter here, so entries that differ solely in capabilities are not a disagreement.
        var distinctRates = candidates
            .DistinctBy(entry => (
                entry.InputCostPer1MUsd,
                entry.OutputCostPer1MUsd,
                entry.CachedInputCostPer1MUsd,
                entry.CacheWriteCostPer1MUsd))
            .ToList();

        if (distinctRates.Count != 1)
        {
            return null;
        }

        var priced = distinctRates[0];
        return priced.InputCostPer1MUsd is null && priced.OutputCostPer1MUsd is null
            ? null
            : new ModelPricing(
                priced.InputCostPer1MUsd,
                priced.OutputCostPer1MUsd,
                priced.CachedInputCostPer1MUsd,
                priced.CacheWriteCostPer1MUsd);
    }

    private static AiPurpose MapCategoryToPurpose(AiConnectionModelCategory category)
    {
        return category switch
        {
            AiConnectionModelCategory.LowEffort => AiPurpose.ReviewLowEffort,
            AiConnectionModelCategory.MediumEffort => AiPurpose.ReviewMediumEffort,
            AiConnectionModelCategory.HighEffort => AiPurpose.ReviewHighEffort,
            AiConnectionModelCategory.Embedding => AiPurpose.EmbeddingDefault,
            AiConnectionModelCategory.MemoryReconsideration => AiPurpose.MemoryReconsideration,
            _ => AiPurpose.ReviewDefault,
        };
    }
}
