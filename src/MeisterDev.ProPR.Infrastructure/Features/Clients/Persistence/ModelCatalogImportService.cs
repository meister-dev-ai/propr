// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Default <see cref="IModelCatalogImportService" />. Reads a snapshot through the provider library's
///     importer and upserts the result as global catalog entries.
/// </summary>
/// <remarks>
///     Two properties are deliberate. Import is scoped to global rows, so a tenant's negotiated pricing and an
///     operator's corrections survive a refresh untouched — they live in the same table under a different scope.
///     And import is an upsert keyed on provider plus model rather than a delete-and-insert, so running it again
///     is harmless and a model that disappears from a newer snapshot is left alone rather than yanked out from
///     under a configuration that still references it.
/// </remarks>
public sealed class ModelCatalogImportService(
    MeisterProPRDbContext db,
    ICatalogSnapshotImporter importer,
    TimeProvider timeProvider) : IModelCatalogImportService
{
    /// <inheritdoc />
    public async Task<int> SeedFromBundledSnapshotAsync(CancellationToken ct = default)
    {
        await using var snapshot = BundledCatalogSnapshot.Open();
        return await this.ImportSnapshotAsync(snapshot, ct);
    }

    /// <inheritdoc />
    public async Task<int> ImportSnapshotAsync(Stream snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var entries = await importer.ImportAsync(snapshot, ct);
        if (entries.Count == 0)
        {
            return 0;
        }

        var existing = await db.AiModelCatalogEntries
            .Where(row => row.TenantId == null && row.ClientId == null)
            .ToDictionaryAsync(row => (row.ProviderId, row.RemoteModelId), ct);

        // Npgsql requires UTC for a timestamptz and throws at run time, not compile time, if given anything else.
        var importedAt = timeProvider.GetUtcNow();
        var written = 0;

        foreach (var entry in entries)
        {
            var key = (entry.ProviderId, entry.RemoteModelId);
            if (existing.TryGetValue(key, out var row))
            {
                Apply(row, entry, importer.SourceFormat, importedAt);
            }
            else
            {
                row = new AiModelCatalogEntryRecord { Id = Guid.NewGuid() };
                Apply(row, entry, importer.SourceFormat, importedAt);
                db.AiModelCatalogEntries.Add(row);
            }

            written++;
        }

        await db.SaveChangesAsync(ct);
        return written;
    }

    private static void Apply(
        AiModelCatalogEntryRecord row,
        ProviderCatalogEntry entry,
        string sourceFormat,
        DateTimeOffset importedAt)
    {
        row.ProviderId = entry.ProviderId;
        row.ProviderName = entry.ProviderName;
        row.RemoteModelId = entry.RemoteModelId;
        row.DisplayName = entry.DisplayName;
        row.Family = entry.Family;
        row.SupportsToolUse = entry.SupportsToolUse;
        row.SupportsStructuredOutput = entry.SupportsStructuredOutput;
        row.SupportsReasoning = entry.SupportsReasoning;
        row.SupportsPromptCaching = entry.SupportsPromptCaching;
        row.ReasoningContentField = entry.ReasoningContentField;
        row.MaxContextTokens = entry.MaxContextTokens;
        row.MaxOutputTokens = entry.MaxOutputTokens;
        row.InputCostPer1MUsd = entry.InputCostPer1MUsd;
        row.OutputCostPer1MUsd = entry.OutputCostPer1MUsd;
        row.CachedInputCostPer1MUsd = entry.CachedInputCostPer1MUsd;
        row.CacheWriteCostPer1MUsd = entry.CacheWriteCostPer1MUsd;
        row.OpenWeights = entry.OpenWeights;
        row.ReleaseDate = entry.ReleaseDate;
        row.SourceFormat = sourceFormat;
        row.ImportedAt = importedAt;
    }
}
