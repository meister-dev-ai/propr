using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for per-client AI connection configurations.</summary>
public sealed class AiConnectionRepository(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IAiConnectionRepository
{
    private static AiConnectionDto ToDto(AiConnectionRecord r) =>
        new(r.Id, r.ClientId, r.DisplayName, r.EndpointUrl, r.Models, r.IsActive, r.ActiveModel, r.CreatedAt,
            r.ModelCategory.HasValue ? (AiConnectionModelCategory)r.ModelCategory.Value : null);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.AiConnections
            .Where(a => a.ClientId == clientId)
            .OrderByDescending(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<AiConnectionDto>)t.Result.Select(ToDto).ToList(), ct);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections
            .Where(a => a.ClientId == clientId && a.IsActive)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddAsync(
        Guid clientId,
        string displayName,
        string endpointUrl,
        IReadOnlyList<string> models,
        string? apiKey,
        AiConnectionModelCategory? modelCategory = null,
        CancellationToken ct = default)
    {
        var record = new AiConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DisplayName = displayName,
            EndpointUrl = endpointUrl,
            Models = models.ToArray(),
            IsActive = false,
            ApiKey = apiKey,
            ModelCategory = modelCategory.HasValue ? (short)modelCategory.Value : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.AiConnections.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid connectionId,
        string? displayName,
        string? endpointUrl,
        IReadOnlyList<string>? models,
        string? apiKey,
        CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        if (displayName is not null)
        {
            record.DisplayName = displayName;
        }

        if (endpointUrl is not null)
        {
            record.EndpointUrl = endpointUrl;
        }

        if (models is not null)
        {
            record.Models = models.ToArray();
        }

        if (apiKey is not null)
        {
            record.ApiKey = apiKey;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        dbContext.AiConnections.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateAsync(Guid connectionId, string model, CancellationToken ct = default)
    {
        var target = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (target is null)
        {
            return false;
        }

        if (!target.Models.Contains(model))
        {
            return false;
        }

        // Deactivate all other connections for the same client in the same transaction.
        var others = await dbContext.AiConnections
            .Where(a => a.ClientId == target.ClientId && a.IsActive && a.Id != connectionId)
            .ToListAsync(ct);

        foreach (var other in others)
        {
            other.IsActive = false;
            other.ActiveModel = null;
        }

        target.IsActive = true;
        target.ActiveModel = model;
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deactivates the specified connection. Returns false if not found.</summary>
    public async Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        record.IsActive = false;
        record.ActiveModel = null;
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uses a short-lived <see cref="MeisterProPRDbContext" /> from the factory so concurrent
    ///     calls from parallel file-review tasks cannot share the same context instance.
    /// </remarks>
    public async Task<AiConnectionDto?> GetForTierAsync(Guid clientId, AiConnectionModelCategory tier, CancellationToken ct = default)
    {
        var category = (short)tier;
        if (contextFactory is not null)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var record = await db.AiConnections
                .AsNoTracking()
                .Where(a => a.ClientId == clientId && a.ModelCategory == category)
                .FirstOrDefaultAsync(ct);
            return record is null ? null : ToDto(record);
        }
        else
        {
            var record = await dbContext.AiConnections
                .AsNoTracking()
                .Where(a => a.ClientId == clientId && a.ModelCategory == category)
                .FirstOrDefaultAsync(ct);
            return record is null ? null : ToDto(record);
        }
    }
}
