using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for per-client AI reviewer finding dismissals.</summary>
public sealed class FindingDismissalRepository(MeisterProPRDbContext dbContext) : IFindingDismissalRepository
{
    private static FindingDismissal ToEntity(FindingDismissalRecord r)
    {
        var entity = new FindingDismissal(r.Id, r.ClientId, r.PatternText, r.Label, r.OriginalMessage);
        return entity;
    }

    private static FindingDismissalRecord ToRecord(FindingDismissal e) =>
        new()
        {
            Id = e.Id,
            ClientId = e.ClientId,
            PatternText = e.PatternText,
            Label = e.Label,
            OriginalMessage = e.OriginalMessage,
            CreatedAt = e.CreatedAt,
        };

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDismissal>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.FindingDismissals
            .Where(d => d.ClientId == clientId)
            .OrderByDescending(d => d.CreatedAt)
            .AsNoTracking()
            .Select(r => new FindingDismissal(r.Id, r.ClientId, r.PatternText, r.Label, r.OriginalMessage))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<FindingDismissal?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dbContext.FindingDismissals
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        return record is null ? null : ToEntity(record);
    }

    /// <inheritdoc />
    public async Task AddAsync(FindingDismissal dismissal, CancellationToken ct = default)
    {
        dbContext.FindingDismissals.Add(ToRecord(dismissal));
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(FindingDismissal dismissal, CancellationToken ct = default)
    {
        var record = await dbContext.FindingDismissals.FindAsync([dismissal.Id], ct);
        if (record is null)
        {
            return;
        }

        record.Label = dismissal.Label;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dbContext.FindingDismissals.FindAsync([id], ct);
        if (record is null)
        {
            return false;
        }

        dbContext.FindingDismissals.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }
}
