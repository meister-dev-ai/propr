// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>Storage for enrolled runners and the tokens that enroll them.</summary>
public sealed class RunnerRegistry(MeisterProPRDbContext dbContext) : IRunnerRegistry
{
    /// <inheritdoc />
    public Task<RunnerRegistrationToken?> FindTokenAsync(string tokenLookupHash, CancellationToken ct = default)
    {
        return dbContext.RunnerRegistrationTokens
            .SingleOrDefaultAsync(token => token.TokenLookupHash == tokenLookupHash, ct);
    }

    /// <inheritdoc />
    public Task<ReviewRunner?> FindByCredentialLookupAsync(
        string credentialLookupHash,
        CancellationToken ct = default)
    {
        return dbContext.ReviewRunners
            .SingleOrDefaultAsync(runner => runner.CredentialLookupHash == credentialLookupHash, ct);
    }

    /// <inheritdoc />
    public Task<ReviewRunner?> FindByIdAsync(Guid runnerId, CancellationToken ct = default)
    {
        return dbContext.ReviewRunners.SingleOrDefaultAsync(runner => runner.Id == runnerId, ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(ReviewRunner runner, RunnerRegistrationToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(token);

        // One save for both. The enrollment and the token use it consumed have to land together, or a
        // crash between them either loses the runner or lets its token be spent twice.
        dbContext.ReviewRunners.Add(runner);
        dbContext.RunnerRegistrationTokens.Update(token);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task AddTokenAsync(RunnerRegistrationToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        dbContext.RunnerRegistrationTokens.Add(token);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunnerRegistrationToken>> ListTokensAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Used-up and expired tokens are left out: they cannot enroll anything, so listing them would bury
        // the ones an operator can still act on.
        var now = DateTimeOffset.UtcNow;
        return await dbContext.RunnerRegistrationTokens
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId
                        && t.RevokedAt == null
                        && (t.ExpiresAt == null || t.ExpiresAt > now)
                        && (t.MaxUses == null || t.UseCount < t.MaxUses))
            .OrderByDescending(t => t.IssuedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<RunnerRegistrationToken?> FindTokenByIdAsync(Guid tokenId, CancellationToken ct = default)
    {
        return dbContext.RunnerRegistrationTokens.FirstOrDefaultAsync(t => t.Id == tokenId, ct);
    }

    /// <inheritdoc />
    public async Task UpdateTokenAsync(RunnerRegistrationToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        dbContext.RunnerRegistrationTokens.Update(token);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRunner>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Revoked runners are listed too. An operator looking at the registry after an incident needs to
        // see what was revoked and when, and a list that quietly drops them answers a different question.
        return await dbContext.ReviewRunners
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListUnseenSinceAsync(
        DateTimeOffset unseenSince,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // Coalesced onto enrollment rather than filtered on a null last-seen, so a host that enrolled and
        // never called in is reaped on the same clock as one that stopped calling in. Ordered oldest first
        // so a capped sweep makes progress from the far end rather than revisiting the same recent rows.
        return await dbContext.ReviewRunners
            .AsNoTracking()
            .Where(r => (r.LastSeenAt ?? r.EnrolledAt) < unseenSince)
            .OrderBy(r => r.LastSeenAt ?? r.EnrolledAt)
            .Select(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ReviewRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        dbContext.ReviewRunners.Update(runner);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task<bool> HoldsLeaseAsync(Guid runnerId, CancellationToken ct = default)
    {
        // The lease owner is the runner id as text, which is the same value the claim writes and the call
        // authorizer checks, so this cannot disagree with them about who holds what.
        var owner = runnerId.ToString("D");
        return dbContext.ReviewJobs
            .AsNoTracking()
            .AnyAsync(j => j.Status == JobStatus.Processing && j.LeaseOwner == owner, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid runnerId, CancellationToken ct = default)
    {
        return await dbContext.ReviewRunners
            .Where(r => r.Id == runnerId)
            .ExecuteDeleteAsync(ct) > 0;
    }
}
