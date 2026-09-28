// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Ports;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>Issues and verifies tenant-bound credentials through Application persistence contracts.</summary>
public sealed class TenantMachineCredentialService(ITenantMachineCredentialStore store, TenantMachineAuthenticationThrottle? throttle = null)
{
    public async Task<IssuedTenantMachineCredential?> IssueAsync(Guid tenantId, string label, DateTimeOffset? expiresAt, Guid actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (expiresAt is not null && expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be in the future.");
        }

        if (!await store.IsTenantActiveAsync(tenantId, ct))
        {
            return null;
        }

        var token = "mprm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var credential = new TenantMachineCredential(
            Guid.NewGuid(), tenantId, label,
            BCrypt.Net.BCrypt.HashPassword(token), LookupHash(token), now, expiresAt?.ToUniversalTime(), actorUserId);
        await store.CreateAsync(credential, ct);
        return new IssuedTenantMachineCredential(credential.Id, tenantId, label, token, now, credential.ExpiresAt);
    }

    public async Task<TenantMachineIdentity?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (!token.StartsWith("mprm_", StringComparison.Ordinal) || token.Length != 69)
        {
            return null;
        }

        var credential = await store.FindActiveAsync(LookupHash(token), DateTimeOffset.UtcNow, ct);
        if (credential is null)
        {
            return null;
        }

        if (throttle is not null && !throttle.TryAcquireCredential(credential.Id))
        {
            throw new TenantMachineCredentialThrottledException();
        }

        if (!BCrypt.Net.BCrypt.Verify(token, credential.TokenHash))
        {
            return null;
        }

        return await store.IsTenantActiveAsync(credential.TenantId, ct)
            ? new TenantMachineIdentity(credential.Id, credential.TenantId)
            : null;
    }

    public Task<bool> OwnsClientAsync(Guid tenantId, Guid clientId, CancellationToken ct) =>
        store.OwnsClientAsync(tenantId, clientId, ct);

    public Task<IReadOnlyList<Guid>> GetClientIdsAsync(Guid tenantId, CancellationToken ct) =>
        store.GetClientIdsAsync(tenantId, ct);

    public Task<Guid?> GetJobClientIdAsync(Guid jobId, CancellationToken ct) =>
        store.GetJobClientIdAsync(jobId, ct);

    public Task<IReadOnlyList<TenantMachineCredentialSummary>> ListAsync(Guid tenantId, CancellationToken ct) =>
        store.ListAsync(tenantId, ct);

    public Task<bool> RevokeAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken ct) =>
        store.RevokeAsync(tenantId, id, actorUserId, ct);

    private static string LookupHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
