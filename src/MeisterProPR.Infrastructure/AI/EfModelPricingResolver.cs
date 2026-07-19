// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     EF Core implementation of <see cref="IModelPricingResolver" />. Reads the configured models for an AI
///     connection from a short-lived <see cref="MeisterProPRDbContext" /> and resolves the pricing of the
///     model a review pass used, matching first by model id (remote id or display name) and then by the
///     purpose bound to the pass's effort tier. Returns <see langword="null" /> whenever the connection,
///     model, or a usable match cannot be found.
/// </summary>
public sealed class EfModelPricingResolver(IDbContextFactory<MeisterProPRDbContext> contextFactory)
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

        return new ModelPricing(
            match.InputCostPer1MUsd,
            match.OutputCostPer1MUsd,
            match.CachedInputCostPer1MUsd);
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
