// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Default <see cref="IConfiguredModelReasoningLevelBackfill" />. Detects the levels of each chat model from the
///     effective catalog of the model's client or tenant and from the add-in of the model's provider family.
/// </summary>
public sealed class ConfiguredModelReasoningLevelBackfill(
    MeisterProPRDbContext db,
    IModelCatalogRepository catalog,
    IAiProviderDriverRegistry providerDrivers) : IConfiguredModelReasoningLevelBackfill
{
    private static readonly string[] RefreshedSources = [nameof(AiReasoningLevelSource.Catalog), nameof(AiReasoningLevelSource.AddIn)];

    private static readonly string ChatKind = nameof(AiOperationKind.Chat);

    /// <inheritdoc />
    public async Task<int> BackfillAllAsync(CancellationToken ct)
    {
        var models = await db.AiConfiguredModels
            .Include(model => model.ConnectionProfile)
            .Where(model => model.ReasoningLevelsSource == null || RefreshedSources.Contains(model.ReasoningLevelsSource))
            .ToListAsync(ct);

        var catalogs = new Dictionary<(Guid? ClientId, Guid? TenantId), IReadOnlyList<AiModelCatalogEntryDto>>();
        var changed = 0;
        foreach (var model in models)
        {
            var connection = model.ConnectionProfile;
            if (connection is null || !model.OperationKinds.Contains(ChatKind, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var owner = (connection.ClientId, connection.TenantId);
            if (!catalogs.TryGetValue(owner, out var entries))
            {
                entries = await this.CatalogOfAsync(connection.ClientId, connection.TenantId, ct);
                catalogs[owner] = entries;
            }

            var driver = providerDrivers.IsRegistered(connection.ProviderKind) ? providerDrivers.GetRequired(connection.ProviderKind) : null;
            var (levels, source) = ConfiguredModelReasoningLevels.WithDefaultLevel(
                ConfiguredModelReasoningLevels.Detect(
                    driver,
                    driver?.Declaration.Key ?? connection.ProviderKind,
                    model.RemoteModelId,
                    entries),
                model.DefaultReasoningLevel);

            // A detected list that no source reports any more stays, because a source that is out of date, for
            // example an older add-in, is no reason to take levels away that a review may rely on.
            if (levels is null)
            {
                continue;
            }

            var sourceName = source?.ToString();
            if (model.ReasoningLevels is { } current
                && current.SequenceEqual(levels, StringComparer.Ordinal)
                && string.Equals(model.ReasoningLevelsSource, sourceName, StringComparison.Ordinal))
            {
                continue;
            }

            model.ReasoningLevels = [.. levels];
            model.ReasoningLevelsSource = sourceName;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private async Task<IReadOnlyList<AiModelCatalogEntryDto>> CatalogOfAsync(Guid? clientId, Guid? tenantId, CancellationToken ct)
    {
        if (clientId is { } client)
        {
            return await catalog.GetEffectiveForClientAsync(client, ct: ct);
        }

        return tenantId is { } tenant ? await catalog.GetEffectiveForTenantAsync(tenant, ct: ct) : [];
    }
}
