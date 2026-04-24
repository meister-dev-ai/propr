// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Implementation of <see cref="IClientAdminService" />.</summary>
public sealed class ClientAdminService(
    MeisterProPRDbContext dbContext,
    IProviderActivationService? providerActivationService = null) : IClientAdminService
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
    public async Task<ClientDto> CreateAsync(string displayName, CancellationToken ct = default)
    {
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
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
    public async Task<IReadOnlyList<ClientDto>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var clients = await dbContext.Clients
            .Where(c => idList.Contains(c.Id))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return clients.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderConnectionAuditEntryDto>> GetProviderConnectionAuditTrailAsync(
        Guid clientId,
        int take = 20,
        CancellationToken ct = default)
    {
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "The take parameter must be greater than zero.");
        }

        await this.PurgeExpiredProviderAuditEntriesAsync(ct);

        var records = await dbContext.ProviderConnectionAuditEntries
            .AsNoTracking()
            .Where(entry => entry.ClientId == clientId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(take)
            .ToListAsync(ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            records = records
                .Where(record => enabledProviders.Contains(record.Provider))
                .ToList();
        }

        return records
            .Select(record => new ProviderConnectionAuditEntryDto(
                record.Id,
                record.ClientId,
                record.ConnectionId,
                record.Provider,
                record.DisplayName,
                record.HostBaseUrl,
                record.EventType,
                record.Summary,
                record.OccurredAt,
                record.Status,
                record.FailureCategory,
                record.Detail))
            .ToList()
            .AsReadOnly();
    }

    private async Task PurgeExpiredProviderAuditEntriesAsync(CancellationToken ct)
    {
        var cutoff = ProviderRetentionPolicy.GetProviderConnectionAuditCutoff(DateTimeOffset.UtcNow);
        var expiredEntries = await dbContext.ProviderConnectionAuditEntries
            .Where(entry => entry.OccurredAt < cutoff)
            .ToListAsync(ct);

        if (expiredEntries.Count == 0)
        {
            return;
        }

        dbContext.ProviderConnectionAuditEntries.RemoveRange(expiredEntries);
        await dbContext.SaveChangesAsync(ct);
    }

    private static ClientDto ToDto(ClientRecord client)
    {
        return new ClientDto(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage);
    }
}
