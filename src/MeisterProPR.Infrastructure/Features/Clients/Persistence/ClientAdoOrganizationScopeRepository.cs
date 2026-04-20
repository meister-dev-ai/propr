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
    public async Task<IReadOnlyList<ClientAdoOrganizationScopeDto>> GetByClientIdAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await dbContext.ClientScmScopes
            .AsNoTracking()
            .Include(scope => scope.Connection)
            .Where(scope => scope.ClientId == clientId)
            .Where(scope => scope.ScopeType == "organization")
            .Where(scope => scope.Connection != null && scope.Connection.Provider == ScmProvider.AzureDevOps)
            .OrderBy(scope => scope.ScopePath)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<ClientAdoOrganizationScopeDto?> GetByIdAsync(
        Guid clientId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmScopes
            .AsNoTracking()
            .Include(scope => scope.Connection)
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        return record is null || record.ScopeType != "organization" ||
               record.Connection?.Provider != ScmProvider.AzureDevOps
            ? null
            : ToDto(record);
    }

    public async Task<ClientAdoOrganizationScopeDto?> AddAsync(
        Guid clientId,
        string organizationUrl,
        string? displayName,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Legacy Azure DevOps organization-scope writes are retired. Use provider scopes instead.");
    }

    public async Task<ClientAdoOrganizationScopeDto?> UpdateAsync(
        Guid clientId,
        Guid scopeId,
        string organizationUrl,
        string? displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Legacy Azure DevOps organization-scope writes are retired. Use provider scopes instead.");
    }

    public async Task<ClientAdoOrganizationScopeDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid scopeId,
        AdoOrganizationVerificationStatus verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmScopes
            .Include(scope => scope.Connection)
            .FirstOrDefaultAsync(scope => scope.ClientId == clientId && scope.Id == scopeId, ct);

        if (record is null || record.ScopeType != "organization" ||
            record.Connection?.Provider != ScmProvider.AzureDevOps)
        {
            return null;
        }

        record.VerificationStatus = MapVerificationStatus(verificationStatus);
        record.LastVerifiedAt = verifiedAt;
        record.LastVerificationError = NormalizeOptional(verificationError);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid clientId, Guid scopeId, CancellationToken ct = default)
    {
        throw new NotSupportedException("Legacy Azure DevOps organization-scope writes are retired. Use provider scopes instead.");
    }

    private static ClientAdoOrganizationScopeDto ToDto(ClientScmScopeRecord record)
    {
        return new ClientAdoOrganizationScopeDto(
            record.Id,
            record.ClientId,
            NormalizeOrganizationUrl(record.ScopePath),
            record.DisplayName,
            record.IsEnabled,
            MapVerificationStatus(record.VerificationStatus),
            record.LastVerifiedAt,
            record.LastVerificationError,
            record.CreatedAt,
            record.UpdatedAt);
    }

    private static string NormalizeOrganizationUrl(string organizationUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        return organizationUrl.Trim().TrimEnd('/');
    }

    private static AdoOrganizationVerificationStatus MapVerificationStatus(string? verificationStatus)
    {
        return verificationStatus?.Trim().ToLowerInvariant() switch
        {
            "verified" => AdoOrganizationVerificationStatus.Verified,
            "failed" => AdoOrganizationVerificationStatus.Unreachable,
            "stale" => AdoOrganizationVerificationStatus.Stale,
            _ => AdoOrganizationVerificationStatus.Unknown,
        };
    }

    private static string MapVerificationStatus(AdoOrganizationVerificationStatus verificationStatus)
    {
        return verificationStatus switch
        {
            AdoOrganizationVerificationStatus.Verified => "verified",
            AdoOrganizationVerificationStatus.Unauthorized or AdoOrganizationVerificationStatus.Unreachable => "failed",
            AdoOrganizationVerificationStatus.Stale => "stale",
            _ => "unknown",
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
