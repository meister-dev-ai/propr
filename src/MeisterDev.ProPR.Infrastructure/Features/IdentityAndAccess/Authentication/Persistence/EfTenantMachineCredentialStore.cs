// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Authentication.Persistence;

/// <summary>EF persistence for machine credentials, audit entries, and current resource ownership.</summary>
public sealed class EfTenantMachineCredentialStore(MeisterProPRDbContext db) : ITenantMachineCredentialStore
{
    private static readonly SemaphoreSlim NonRelationalRevokeGate = new(1, 1);

    public Task<bool> IsTenantActiveAsync(Guid tenantId, CancellationToken ct) =>
        db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId && tenant.IsActive, ct);

    public async Task CreateAsync(TenantMachineCredential credential, CancellationToken ct)
    {
        db.TenantMachineCredentials.Add(
            new TenantMachineCredentialRecord
            {
                Id = credential.Id,
                TenantId = credential.TenantId,
                Label = credential.Label,
                TokenHash = credential.TokenHash,
                TokenLookupHash = credential.TokenLookupHash,
                CreatedAt = credential.CreatedAt,
                ExpiresAt = credential.ExpiresAt,
                IssuedByUserId = credential.IssuedByUserId,
            });
        AddAudit(credential.TenantId, credential.IssuedByUserId, "machine_credential_issued", credential.Id, credential.CreatedAt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<TenantMachineCredential?> FindActiveAsync(string lookupHash, DateTimeOffset now, CancellationToken ct) =>
        db.TenantMachineCredentials.AsNoTracking()
            .Where(record => record.TokenLookupHash == lookupHash && record.RevokedAt == null &&
                             (record.ExpiresAt == null || record.ExpiresAt > now))
            .Select(record => new TenantMachineCredential(
                record.Id, record.TenantId, record.Label,
                record.TokenHash, record.TokenLookupHash, record.CreatedAt, record.ExpiresAt, record.IssuedByUserId))
            .SingleOrDefaultAsync(ct);

    public Task<Guid?> GetJobClientIdAsync(Guid jobId, CancellationToken ct) =>
        db.ReviewJobs.AsNoTracking().Where(job => job.Id == jobId)
            .Select(job => (Guid?)job.ClientId).SingleOrDefaultAsync(ct);

    public Task<bool> OwnsClientAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        return db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId && c.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetClientIdsAsync(Guid tenantId, CancellationToken ct)
    {
        return await db.Clients.AsNoTracking().Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantMachineCredentialSummary>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        return await db.TenantMachineCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new TenantMachineCredentialSummary(x.Id, x.TenantId, x.Label, x.CreatedAt, x.ExpiresAt, x.RevokedAt))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await NonRelationalRevokeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await this.RevokeInNonRelationalStoreAsync(tenantId, id, actorUserId, ct).ConfigureAwait(false);
            }
            finally
            {
                NonRelationalRevokeGate.Release();
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var updated = await db.TenantMachineCredentials
            .Where(x => x.Id == id && x.TenantId == tenantId && x.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, (DateTimeOffset?)now)
                    .SetProperty(x => x.RevokedByUserId, (Guid?)actorUserId), ct).ConfigureAwait(false);
        if (updated == 0)
        {
            return await db.TenantMachineCredentials.AsNoTracking()
                .AnyAsync(x => x.Id == id && x.TenantId == tenantId, ct).ConfigureAwait(false);
        }

        AddAudit(tenantId, actorUserId, "machine_credential_revoked", id, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RevokeInNonRelationalStoreAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken ct)
    {
        var record = await db.TenantMachineCredentials
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        if (record.RevokedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            record.RevokedAt = now;
            record.RevokedByUserId = actorUserId;
            AddAudit(tenantId, actorUserId, "machine_credential_revoked", id, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    private void AddAudit(Guid tenantId, Guid actorUserId, string eventType, Guid credentialId, DateTimeOffset now)
    {
        db.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ActorUserId = actorUserId,
                EventType = eventType, Summary = $"Machine credential {credentialId}", OccurredAt = now,
            });
    }
}
