// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;

namespace MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Ports;

/// <summary>Persists machine credential lifecycle events and reads current resource ownership.</summary>
public interface ITenantMachineCredentialStore
{
    Task<bool> IsTenantActiveAsync(Guid tenantId, CancellationToken ct);
    Task CreateAsync(TenantMachineCredential credential, CancellationToken ct);
    Task<TenantMachineCredential?> FindActiveAsync(string lookupHash, DateTimeOffset now, CancellationToken ct);
    Task<bool> OwnsClientAsync(Guid tenantId, Guid clientId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> GetClientIdsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid?> GetJobClientIdAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<TenantMachineCredentialSummary>> ListAsync(Guid tenantId, CancellationToken ct);
    Task<bool> RevokeAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken ct);
}
