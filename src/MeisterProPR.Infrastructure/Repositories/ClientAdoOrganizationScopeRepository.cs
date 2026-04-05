// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for client-scoped Azure DevOps organization scopes.</summary>
public sealed class ClientAdoOrganizationScopeRepository(MeisterProPRDbContext dbContext)
    : IClientAdoOrganizationScopeRepository
{
    private static ClientAdoOrganizationScopeDto ToDto(ClientAdoOrganizationScopeRecord record) =>
        new(
            record.Id,
            record.ClientId,
            record.OrganizationUrl,
            record.DisplayName,
            record.IsEnabled,
            record.VerificationStatus,
            record.LastVerifiedAt,
            record.LastVerificationError,
            record.CreatedAt,
            record.UpdatedAt);

    public async Task<IReadOnlyList<ClientAdoOrganizationScopeDto>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        var records = await dbContext.ClientAdoOrganizationScopes
            .AsNoTracking()
            .Where(scope => scope.ClientId == clientId)
            .OrderBy(scope => scope.OrganizationUrl)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<ClientAdoOrganizationScopeDto?> GetByIdAsync(Guid clientId, Guid scopeId, CancellationToken ct = default)
    {
        var record = await dbContext.ClientAdoOrganizationScopes
            .AsNoTracking()
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        return record is null ? null : ToDto(record);
    }

    public async Task<ClientAdoOrganizationScopeDto?> AddAsync(
        Guid clientId,
        string organizationUrl,
        string? displayName,
        CancellationToken ct = default)
    {
        if (!await dbContext.Clients.AnyAsync(client => client.Id == clientId, ct))
        {
            return null;
        }

        var normalizedOrganizationUrl = NormalizeOrganizationUrl(organizationUrl);
        if (await dbContext.ClientAdoOrganizationScopes.AnyAsync(
                scope => scope.ClientId == clientId && scope.OrganizationUrl == normalizedOrganizationUrl,
                ct))
        {
            throw new InvalidOperationException("An organization scope for this URL already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new ClientAdoOrganizationScopeRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            OrganizationUrl = normalizedOrganizationUrl,
            DisplayName = NormalizeOptional(displayName),
            IsEnabled = true,
            VerificationStatus = AdoOrganizationVerificationStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.ClientAdoOrganizationScopes.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<ClientAdoOrganizationScopeDto?> UpdateAsync(
        Guid clientId,
        Guid scopeId,
        string organizationUrl,
        string? displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientAdoOrganizationScopes
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        if (record is null)
        {
            return null;
        }

        var normalizedOrganizationUrl = NormalizeOrganizationUrl(organizationUrl);
        if (await dbContext.ClientAdoOrganizationScopes.AnyAsync(
                scope => scope.ClientId == clientId && scope.Id != scopeId && scope.OrganizationUrl == normalizedOrganizationUrl,
                ct))
        {
            throw new InvalidOperationException("An organization scope for this URL already exists.");
        }

        record.OrganizationUrl = normalizedOrganizationUrl;
        record.DisplayName = NormalizeOptional(displayName);
        record.IsEnabled = isEnabled;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<ClientAdoOrganizationScopeDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid scopeId,
        AdoOrganizationVerificationStatus verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientAdoOrganizationScopes
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        if (record is null)
        {
            return null;
        }

        record.VerificationStatus = verificationStatus;
        record.LastVerifiedAt = verifiedAt;
        record.LastVerificationError = NormalizeOptional(verificationError);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid clientId, Guid scopeId, CancellationToken ct = default)
    {
        var record = await dbContext.ClientAdoOrganizationScopes
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        if (record is null)
        {
            return false;
        }

        dbContext.ClientAdoOrganizationScopes.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static string NormalizeOrganizationUrl(string organizationUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        return organizationUrl.Trim().TrimEnd('/');
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
