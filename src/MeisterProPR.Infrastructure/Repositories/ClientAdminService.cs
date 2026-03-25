using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Implementation of <see cref="IClientAdminService" />.</summary>
public sealed class ClientAdminService(MeisterProPRDbContext dbContext) : IClientAdminService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientDto>> GetAllAsync(CancellationToken ct = default)
    {
        var clients = await dbContext.Clients
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return clients.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<ClientDto?> GetByIdAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await dbContext.Clients.FindAsync([clientId], ct);
        return client is null ? null : ToDto(client);
    }

    /// <inheritdoc />
    public async Task<ClientDto?> CreateAsync(string key, string displayName, CancellationToken ct = default)
    {
        var exists = await dbContext.Clients.AnyAsync(c => c.Key == key, ct);
        if (exists)
        {
            return null;
        }

        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = key,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(client);
    }

    /// <inheritdoc />
    public async Task<ClientDto?> PatchAsync(
        Guid clientId,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior = null,
        string? customSystemMessage = null,
        CancellationToken ct = default)
    {
        var client = await dbContext.Clients.FindAsync([clientId], ct);
        if (client is null)
        {
            return null;
        }

        if (isActive.HasValue)
        {
            client.IsActive = isActive.Value;
        }

        if (displayName is not null)
        {
            client.DisplayName = displayName;
        }

        if (commentResolutionBehavior.HasValue)
        {
            client.CommentResolutionBehavior = commentResolutionBehavior.Value;
        }

        if (customSystemMessage is not null)
        {
            // Empty string clears the stored value; any other non-null value sets it.
            client.CustomSystemMessage = string.IsNullOrEmpty(customSystemMessage)
                ? null
                : customSystemMessage;
        }

        await dbContext.SaveChangesAsync(ct);
        return ToDto(client);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await dbContext.Clients.FindAsync([clientId], ct);
        if (client is null)
        {
            return false;
        }

        dbContext.Clients.Remove(client);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid clientId, CancellationToken ct = default)
    {
        return dbContext.Clients.AnyAsync(c => c.Id == clientId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> SetReviewerIdentityAsync(Guid clientId, Guid reviewerId, CancellationToken ct = default)
    {
        var client = await dbContext.Clients.FindAsync([clientId], ct);
        if (client is null)
        {
            return false;
        }

        client.ReviewerId = reviewerId;
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static ClientDto ToDto(ClientRecord client)
    {
        return new ClientDto(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client is { AdoTenantId: not null, AdoClientId: not null, AdoClientSecret: not null },
            client.AdoTenantId,
            client.AdoClientId,
            client.ReviewerId,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage);
    }
}
