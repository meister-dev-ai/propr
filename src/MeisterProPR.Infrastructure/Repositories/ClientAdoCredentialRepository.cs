// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation that reads/writes the three nullable ADO credential columns on <c>clients</c>.</summary>
public sealed class ClientAdoCredentialRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec)
    : IClientAdoCredentialRepository
{
    private const string SecretPurpose = "ClientAdoCredentials";

    /// <inheritdoc />
    public async Task<ClientAdoCredentials?> GetByClientIdAsync(Guid clientId, CancellationToken ct)
    {
        var record = await dbContext.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clientId, ct);

        if (record is null ||
            string.IsNullOrWhiteSpace(record.AdoTenantId) ||
            string.IsNullOrWhiteSpace(record.AdoClientId) ||
            string.IsNullOrWhiteSpace(record.AdoClientSecret))
        {
            return null;
        }

        return new ClientAdoCredentials(
            record.AdoTenantId,
            record.AdoClientId,
            secretProtectionCodec.Unprotect(record.AdoClientSecret, SecretPurpose));
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Guid clientId, ClientAdoCredentials credentials, CancellationToken ct)
    {
        var record = await dbContext.Clients.FindAsync([clientId], ct);
        if (record is null)
        {
            return;
        }

        record.AdoTenantId = credentials.TenantId;
        record.AdoClientId = credentials.ClientId;
        record.AdoClientSecret = secretProtectionCodec.Protect(credentials.Secret, SecretPurpose);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid clientId, CancellationToken ct)
    {
        var record = await dbContext.Clients.FindAsync([clientId], ct);
        if (record is null)
        {
            return;
        }

        record.AdoTenantId = null;
        record.AdoClientId = null;
        record.AdoClientSecret = null;
        await dbContext.SaveChangesAsync(ct);
    }
}
