// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
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
public sealed class ModelCatalogRepository(MeisterProPRDbContext db, TimeProvider timeProvider) : IModelCatalogRepository
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiModelCatalogEntryDto>> GetEffectiveForTenantAsync(
        Guid tenantId,
        string? providerId = null,
        CancellationToken ct = default)
    {
        // The tenant's own view: global rows plus its own overrides. A client override is deliberately excluded,
        // since it is narrower than the scope being edited here and would misreport what the tenant has set.
        var query = db.AiModelCatalogEntries
            .AsNoTracking()
            .Where(row => (row.TenantId == null && row.ClientId == null) || row.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            query = query.Where(row => row.ProviderId == providerId);
        }

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(row => (row.ProviderId, row.RemoteModelId))
            .Select(group => Resolve(group, clientId: null, tenantId))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiModelCatalogOverrideDto>> GetTenantOverridesAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        var rows = await db.AiModelCatalogEntries
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .OrderBy(row => row.ProviderId)
            .ThenBy(row => row.RemoteModelId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(row => new AiModelCatalogOverrideDto(
                row.ProviderId,
                row.RemoteModelId,
                row.DisplayName,
                row.InputCostPer1MUsd,
                row.OutputCostPer1MUsd,
                row.CachedInputCostPer1MUsd,
                row.CacheWriteCostPer1MUsd))
            .ToList();
    }

    /// <inheritdoc />
    public async Task UpsertTenantOverrideAsync(
        Guid tenantId,
        AiModelCatalogOverrideDto @override,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@override);

        var row = await db.AiModelCatalogEntries
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId
                             && candidate.ProviderId == @override.ProviderId
                             && candidate.RemoteModelId == @override.RemoteModelId,
                ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            // The override inherits identity from the global row it shadows, so a tenant naming a price for a
            // model it has not otherwise configured does not need to restate the model.
            var global = await db.AiModelCatalogEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.TenantId == null
                                 && candidate.ClientId == null
                                 && candidate.ProviderId == @override.ProviderId
                                 && candidate.RemoteModelId == @override.RemoteModelId,
                    ct)
                .ConfigureAwait(false);

            row = new AiModelCatalogEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProviderId = @override.ProviderId,
                RemoteModelId = @override.RemoteModelId,
                ProviderName = global?.ProviderName ?? @override.ProviderId,
                SourceFormat = "operator",
            };
            db.AiModelCatalogEntries.Add(row);
        }

        row.DisplayName = @override.DisplayName ?? row.DisplayName;
        if (string.IsNullOrWhiteSpace(row.DisplayName))
        {
            row.DisplayName = @override.RemoteModelId;
        }

        row.InputCostPer1MUsd = @override.InputCostPer1MUsd;
        row.OutputCostPer1MUsd = @override.OutputCostPer1MUsd;
        row.CachedInputCostPer1MUsd = @override.CachedInputCostPer1MUsd;
        row.CacheWriteCostPer1MUsd = @override.CacheWriteCostPer1MUsd;
        row.ImportedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertTenantModelDefinitionAsync(
        Guid tenantId,
        AiModelCatalogDefinitionDto definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // A model the snapshot already describes takes its capabilities from the snapshot, so accepting a
        // definition for it would quietly discard the capability values the operator just entered.
        var describedGlobally = await db.AiModelCatalogEntries
            .AsNoTracking()
            .AnyAsync(
                row => row.TenantId == null
                       && row.ClientId == null
                       && row.ProviderId == definition.ProviderId
                       && row.RemoteModelId == definition.RemoteModelId,
                ct)
            .ConfigureAwait(false);

        if (describedGlobally)
        {
            throw new ModelCatalogDefinitionConflictException(definition.ProviderId, definition.RemoteModelId);
        }

        var row = await db.AiModelCatalogEntries
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId
                             && candidate.ProviderId == definition.ProviderId
                             && candidate.RemoteModelId == definition.RemoteModelId,
                ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new AiModelCatalogEntryRecord { Id = Guid.NewGuid(), TenantId = tenantId };
            db.AiModelCatalogEntries.Add(row);
        }

        row.ProviderId = definition.ProviderId;
        row.RemoteModelId = definition.RemoteModelId;
        row.ProviderName = definition.ProviderId;
        row.DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName)
            ? definition.RemoteModelId
            : definition.DisplayName;
        row.Family = definition.Family;
        row.SupportsToolUse = definition.SupportsToolUse;
        row.SupportsStructuredOutput = definition.SupportsStructuredOutput;
        row.SupportsReasoning = definition.SupportsReasoning;
        row.SupportsPromptCaching = definition.SupportsPromptCaching;
        row.ReasoningContentField = definition.ReasoningContentField;
        row.MaxContextTokens = definition.MaxContextTokens;
        row.MaxOutputTokens = definition.MaxOutputTokens;
        row.InputCostPer1MUsd = definition.InputCostPer1MUsd;
        row.OutputCostPer1MUsd = definition.OutputCostPer1MUsd;
        row.CachedInputCostPer1MUsd = definition.CachedInputCostPer1MUsd;
        row.CacheWriteCostPer1MUsd = definition.CacheWriteCostPer1MUsd;
        row.SourceFormat = "operator";
        row.ImportedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTenantOverrideAsync(
        Guid tenantId,
        string providerId,
        string remoteModelId,
        CancellationToken ct = default)
    {
        var removed = await db.AiModelCatalogEntries
            .Where(row => row.TenantId == tenantId && row.ProviderId == providerId && row.RemoteModelId == remoteModelId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return removed > 0;
    }

    // clientId is null when resolving for a tenant rather than a client: there is no client layer to apply, and
    // saying so explicitly avoids relying on no real client ever having the empty id.
    private static AiModelCatalogEntryDto? Resolve(
        IEnumerable<AiModelCatalogEntryRecord> rows,
        Guid? clientId,
        Guid? tenantId)
    {
        var candidates = rows.ToList();
        var global = candidates.Find(row => row.TenantId is null && row.ClientId is null);
        var tenantRow = tenantId is null ? null : candidates.Find(row => row.TenantId == tenantId);
        var clientRow = clientId is null ? null : candidates.Find(row => row.ClientId == clientId);

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
