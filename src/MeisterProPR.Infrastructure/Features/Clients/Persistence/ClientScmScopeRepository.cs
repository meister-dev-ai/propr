// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for client-scoped SCM provider scopes.</summary>
public sealed class ClientScmScopeRepository(MeisterProPRDbContext dbContext) : IClientScmScopeRepository
{
    public async Task<IReadOnlyList<ClientScmScopeDto>> GetByConnectionIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var records = await dbContext.ClientScmScopes
            .AsNoTracking()
            .Where(scope => scope.ClientId == clientId && scope.ConnectionId == connectionId)
            .OrderBy(scope => scope.DisplayName)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<ClientScmScopeDto?> GetByIdAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmScopes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                scope => scope.ClientId == clientId && scope.ConnectionId == connectionId && scope.Id == scopeId,
                ct);

        return record is null ? null : ToDto(record);
    }

    public async Task<ClientScmScopeDto?> AddAsync(
        Guid clientId,
        Guid connectionId,
        string scopeType,
        string externalScopeId,
        string scopePath,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        if (!await dbContext.ClientScmConnections.AnyAsync(
                connection => connection.ClientId == clientId && connection.Id == connectionId,
                ct))
        {
            return null;
        }

        var normalizedExternalScopeId = NormalizeRequired(externalScopeId);
        if (await dbContext.ClientScmScopes.AnyAsync(
                scope => scope.ConnectionId == connectionId && scope.ExternalScopeId == normalizedExternalScopeId,
                ct))
        {
            throw new InvalidOperationException("A provider scope for this connection and external scope already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new ClientScmScopeRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ConnectionId = connectionId,
            ScopeType = NormalizeRequired(scopeType),
            ExternalScopeId = normalizedExternalScopeId,
            ScopePath = NormalizeRequired(scopePath),
            DisplayName = NormalizeRequired(displayName),
            VerificationStatus = "unknown",
            IsEnabled = isEnabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.ClientScmScopes.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<ClientScmScopeDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmScopes
            .FirstOrDefaultAsync(
                scope => scope.ClientId == clientId && scope.ConnectionId == connectionId && scope.Id == scopeId,
                ct);

        if (record is null)
        {
            return null;
        }

        record.DisplayName = NormalizeRequired(displayName);
        record.IsEnabled = isEnabled;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid clientId, Guid connectionId, Guid scopeId, CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmScopes
            .FirstOrDefaultAsync(
                scope => scope.ClientId == clientId && scope.ConnectionId == connectionId && scope.Id == scopeId,
                ct);

        if (record is null)
        {
            return false;
        }

        dbContext.ClientScmScopes.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static ClientScmScopeDto ToDto(ClientScmScopeRecord record)
    {
        return new ClientScmScopeDto(
            record.Id,
            record.ClientId,
            record.ConnectionId,
            record.ScopeType,
            record.ExternalScopeId,
            record.ScopePath,
            record.DisplayName,
            record.VerificationStatus,
            record.IsEnabled,
            record.LastVerifiedAt,
            record.LastVerificationError,
            record.CreatedAt,
            record.UpdatedAt);
    }

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
