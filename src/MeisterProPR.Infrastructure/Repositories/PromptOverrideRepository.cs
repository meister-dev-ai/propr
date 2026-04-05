// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for per-client and per-crawl-config AI prompt overrides.</summary>
public sealed class PromptOverrideRepository(MeisterProPRDbContext dbContext) : IPromptOverrideRepository
{
    private static PromptOverride ToEntity(PromptOverrideRecord r) =>
        new(r.Id, r.ClientId, r.CrawlConfigId, r.Scope, r.PromptKey, r.OverrideText);

    private static PromptOverrideRecord ToRecord(PromptOverride e) =>
        new()
        {
            Id = e.Id,
            ClientId = e.ClientId,
            CrawlConfigId = e.CrawlConfigId,
            Scope = e.Scope,
            PromptKey = e.PromptKey,
            OverrideText = e.OverrideText,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
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
                x => x.ClientId == clientId
                     && x.Scope == scope
                     && x.CrawlConfigId == crawlConfigId
                     && x.PromptKey == promptKey,
                ct);

        return record is null ? null : ToEntity(record);
    }

    /// <inheritdoc />
    public async Task<PromptOverride?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dbContext.PromptOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        return record is null ? null : ToEntity(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptOverride>> ListByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.PromptOverrides
            .Where(x => x.ClientId == clientId)
            .OrderBy(x => x.CreatedAt)
            .AsNoTracking()
            .Select(r => new PromptOverride(r.Id, r.ClientId, r.CrawlConfigId, r.Scope, r.PromptKey, r.OverrideText))
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
