// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for configured client reviewer identities.</summary>
public sealed class ClientReviewerIdentityRepository(MeisterProPRDbContext dbContext)
    : IClientReviewerIdentityRepository
{
    public async Task<ClientReviewerIdentityDto?> GetByConnectionIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientReviewerIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                identity => identity.ClientId == clientId && identity.ConnectionId == connectionId,
                ct);

        return record is null ? null : ToDto(record);
    }

    public async Task<ClientReviewerIdentityDto?> UpsertAsync(
        Guid clientId,
        Guid connectionId,
        ScmProvider providerFamily,
        string externalUserId,
        string login,
        string displayName,
        bool isBot,
        CancellationToken ct = default)
    {
        if (!await dbContext.ClientScmConnections.AnyAsync(
                connection => connection.ClientId == clientId && connection.Id == connectionId,
                ct))
        {
            return null;
        }

        var record = await dbContext.ClientReviewerIdentities
            .FirstOrDefaultAsync(
                identity => identity.ClientId == clientId && identity.ConnectionId == connectionId,
                ct);
        var now = DateTimeOffset.UtcNow;

        if (record is null)
        {
            record = new ClientReviewerIdentityRecord
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ConnectionId = connectionId,
                Provider = providerFamily,
                ExternalUserId = NormalizeRequired(externalUserId),
                Login = NormalizeRequired(login),
                DisplayName = NormalizeRequired(displayName),
                IsBot = isBot,
                UpdatedAt = now,
            };

            dbContext.ClientReviewerIdentities.Add(record);
        }
        else
        {
            record.Provider = providerFamily;
            record.ExternalUserId = NormalizeRequired(externalUserId);
            record.Login = NormalizeRequired(login);
            record.DisplayName = NormalizeRequired(displayName);
            record.IsBot = isBot;
            record.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.ClientReviewerIdentities
            .FirstOrDefaultAsync(
                identity => identity.ClientId == clientId && identity.ConnectionId == connectionId,
                ct);

        if (record is null)
        {
            return false;
        }

        dbContext.ClientReviewerIdentities.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static ClientReviewerIdentityDto ToDto(ClientReviewerIdentityRecord record)
    {
        return new ClientReviewerIdentityDto(
            record.Id,
            record.ClientId,
            record.ConnectionId,
            record.Provider,
            record.ExternalUserId,
            record.Login,
            record.DisplayName,
            record.IsBot,
            record.UpdatedAt);
    }

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
