// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.PromptCustomization.Persistence;

/// <summary>Database-backed repository for per-client and per-crawl-config AI prompt overrides.</summary>
public sealed class PromptOverrideRepository(MeisterProPRDbContext dbContext) : IPromptOverrideRepository
{
    private static PromptOverride ToEntity(PromptOverrideRecord record) =>
        new(record.Id, record.ClientId, record.CrawlConfigId, record.Scope, record.PromptKey, record.OverrideText);

    private static PromptOverrideRecord ToRecord(PromptOverride entity) =>
        new()
        {
            Id = entity.Id,
            ClientId = entity.ClientId,
            CrawlConfigId = entity.CrawlConfigId,
            Scope = entity.Scope,
            PromptKey = entity.PromptKey,
            OverrideText = entity.OverrideText,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };

    /// <inheritdoc />
    public async Task<PromptOverride?> GetByScopeAsync(
        Guid clientId,
        PromptOverrideScope scope,
        Guid? crawlConfigId,
        string promptKey,
        CancellationToken ct = default)
    {
        var record = await dbContext.PromptOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(
                current => current.ClientId == clientId
                           && current.Scope == scope
                           && current.CrawlConfigId == crawlConfigId
                           && current.PromptKey == promptKey,
                ct);

        return record is null ? null : ToEntity(record);
    }

    /// <inheritdoc />
    public async Task<PromptOverride?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dbContext.PromptOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == id, ct);

        return record is null ? null : ToEntity(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptOverride>> ListByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.PromptOverrides
            .Where(current => current.ClientId == clientId)
            .OrderBy(current => current.CreatedAt)
            .AsNoTracking()
            .Select(record => new PromptOverride(record.Id, record.ClientId, record.CrawlConfigId, record.Scope, record.PromptKey, record.OverrideText))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(PromptOverride promptOverride, CancellationToken ct = default)
    {
        dbContext.PromptOverrides.Add(ToRecord(promptOverride));
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new DuplicatePromptOverrideException();
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PromptOverride promptOverride, CancellationToken ct = default)
    {
        var record = await dbContext.PromptOverrides.FindAsync([promptOverride.Id], ct);
        if (record is null)
        {
            return;
        }

        record.OverrideText = promptOverride.OverrideText;
        record.UpdatedAt = promptOverride.UpdatedAt;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dbContext.PromptOverrides.FindAsync([id], ct);
        if (record is null)
        {
            return false;
        }

        dbContext.PromptOverrides.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }
}
