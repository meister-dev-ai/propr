// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Default <see cref="IModelCatalogRepository" />. Resolves the three scope layers of the catalog into the
///     single view a client sees.
/// </summary>
/// <remarks>
///     <para>
///         Overrides merge onto the global row rather than replacing it, and only over the values that can
///         genuinely differ per customer: price. A capability cannot — whether a model supports tool use is a fact
///         about the model, not about who is paying for it — so capabilities always come from the global snapshot
///         when one exists. That asymmetry is what lets an operator record a negotiated rate by entering one
///         number instead of restating a model's whole specification.
///     </para>
///     <para>
///         A model with no global row at all is an operator-defined entry; there is nothing to merge onto, so the
///         scoped row stands alone and supplies its own capabilities.
///     </para>
/// </remarks>
public sealed class ModelCatalogRepository(MeisterProPRDbContext db) : IModelCatalogRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AiModelCatalogEntryDto>> GetEffectiveForClientAsync(
        Guid clientId,
        string? providerId = null,
        CancellationToken ct = default)
    {
        var tenantId = await db.Clients
            .Where(client => client.Id == clientId)
            .Select(client => (Guid?)client.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Only this client's own row and its own tenant's rows are considered. A sibling tenant's negotiated
        // pricing is not merely unused here, it is never read.
        var query = db.AiModelCatalogEntries
            .AsNoTracking()
            .Where(row =>
                (row.TenantId == null && row.ClientId == null)
                || row.ClientId == clientId
                || (tenantId != null && row.TenantId == tenantId));

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            query = query.Where(row => row.ProviderId == providerId);
        }

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(row => (row.ProviderId, row.RemoteModelId))
            .Select(group => Resolve(group, clientId, tenantId))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string ProviderId, string ProviderName, int ModelCount)>> GetProvidersAsync(CancellationToken ct = default)
    {
        var grouped = await db.AiModelCatalogEntries
            .AsNoTracking()
            .Where(row => row.TenantId == null && row.ClientId == null)
            .GroupBy(row => new { row.ProviderId, row.ProviderName })
            .Select(group => new { group.Key.ProviderId, group.Key.ProviderName, ModelCount = group.Count() })
            .OrderBy(group => group.ProviderId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return grouped
            .Select(group => (group.ProviderId, group.ProviderName, group.ModelCount))
            .ToList();
    }

    private static AiModelCatalogEntryDto? Resolve(
        IEnumerable<AiModelCatalogEntryRecord> rows,
        Guid clientId,
        Guid? tenantId)
    {
        var candidates = rows.ToList();
        var global = candidates.Find(row => row.TenantId is null && row.ClientId is null);
        var tenantRow = tenantId is null ? null : candidates.Find(row => row.TenantId == tenantId);
        var clientRow = candidates.Find(row => row.ClientId == clientId);

        // Capabilities come from the snapshot when there is one; an operator-defined model has only itself.
        var baseRow = global ?? tenantRow ?? clientRow;
        if (baseRow is null)
        {
            return null;
        }

        // Narrowest scope first: a client's own rate beats its tenant's, which beats list price.
        var (input, layer) = FirstStated(
            clientRow?.InputCostPer1MUsd,
            tenantRow?.InputCostPer1MUsd,
            global?.InputCostPer1MUsd);

        return new AiModelCatalogEntryDto(
            baseRow.ProviderId,
            baseRow.ProviderName,
            baseRow.RemoteModelId,
            clientRow?.DisplayName ?? tenantRow?.DisplayName ?? baseRow.DisplayName,
            baseRow.Family,
            baseRow.SupportsToolUse,
            baseRow.SupportsStructuredOutput,
            baseRow.SupportsReasoning,
            baseRow.SupportsPromptCaching,
            baseRow.ReasoningContentField,
            baseRow.MaxContextTokens,
            baseRow.MaxOutputTokens,
            input,
            FirstStated(clientRow?.OutputCostPer1MUsd, tenantRow?.OutputCostPer1MUsd, global?.OutputCostPer1MUsd).Value,
            FirstStated(clientRow?.CachedInputCostPer1MUsd, tenantRow?.CachedInputCostPer1MUsd, global?.CachedInputCostPer1MUsd).Value,
            FirstStated(clientRow?.CacheWriteCostPer1MUsd, tenantRow?.CacheWriteCostPer1MUsd, global?.CacheWriteCostPer1MUsd).Value,
            baseRow.OpenWeights,
            baseRow.ReleaseDate,
            layer);
    }

    // A null in an override means "inherit", not "free", so the first STATED value wins and the layer that
    // stated it is reported alongside.
    private static (decimal? Value, AiModelCatalogLayer Layer) FirstStated(
        decimal? client,
        decimal? tenant,
        decimal? global)
    {
        if (client.HasValue)
        {
            return (client, AiModelCatalogLayer.ClientOverride);
        }

        return tenant.HasValue
            ? (tenant, AiModelCatalogLayer.TenantOverride)
            : (global, AiModelCatalogLayer.Global);
    }
}
