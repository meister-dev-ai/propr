// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The refresh token and personal access token repositories of a test host on the in-memory database. The
///     repositories revoke with a bulk update, which the in-memory provider does not translate, so these set the same
///     column row by row.
/// </summary>
internal static class RowByRowRevocations
{
    /// <summary>Registers both repositories in place of the ones of the host.</summary>
    /// <param name="services">The services of the test host.</param>
    /// <param name="takeRefreshTokenRevocationFailure">
    ///     Returns whether the next revocation of refresh tokens fails, as a database failure in the middle of a request
    ///     would; <see langword="null" /> for revocations that never fail.
    /// </param>
    public static void Register(IServiceCollection services, Func<bool>? takeRefreshTokenRevocationFailure = null)
    {
        var takeFailure = takeRefreshTokenRevocationFailure ?? (() => false);
        services.RemoveAll<IRefreshTokenRepository>();
        services.AddScoped<RefreshTokenRepository>();
        services.AddScoped<IRefreshTokenRepository>(serviceProvider => new RefreshTokens(
            serviceProvider.GetRequiredService<MeisterProPRDbContext>(),
            serviceProvider.GetRequiredService<RefreshTokenRepository>(),
            takeFailure));
        services.RemoveAll<IUserPatRepository>();
        services.AddScoped<UserPatRepository>();
        services.AddScoped<IUserPatRepository, Pats>();
    }

    private sealed class RefreshTokens(
        MeisterProPRDbContext database,
        RefreshTokenRepository repository,
        Func<bool> takeRevocationFailure)
        : IRefreshTokenRepository
    {
        public Task AddAsync(RefreshToken token, CancellationToken ct = default)
        {
            return repository.AddAsync(token, ct);
        }

        public Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default)
        {
            return repository.GetActiveByHashAsync(tokenHash, ct);
        }

        public Task TouchLastUsedAsync(Guid id, DateTimeOffset lastUsedAt, CancellationToken ct = default)
        {
            return repository.TouchLastUsedAsync(id, lastUsedAt, ct);
        }

        public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            if (takeRevocationFailure())
            {
                throw new InvalidOperationException("The revocation of the refresh tokens failed.");
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var token in await database.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct))
            {
                token.RevokedAt = now;
            }

            await database.SaveChangesAsync(ct);
        }
    }

    private sealed class Pats(MeisterProPRDbContext database, UserPatRepository repository) : IUserPatRepository
    {
        public Task AddAsync(UserPat pat, CancellationToken ct = default)
        {
            return repository.AddAsync(pat, ct);
        }

        public Task<UserPat?> GetActiveByRawTokenAsync(string rawToken, CancellationToken ct = default)
        {
            return repository.GetActiveByRawTokenAsync(rawToken, ct);
        }

        public Task<IReadOnlyList<UserPat>> ListForUserAsync(Guid userId, CancellationToken ct = default)
        {
            return repository.ListForUserAsync(userId, ct);
        }

        public Task RevokeAsync(Guid patId, Guid userId, CancellationToken ct = default)
        {
            return repository.RevokeAsync(patId, userId, ct);
        }

        public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            foreach (var pat in await database.UserPats.Where(p => p.UserId == userId && !p.IsRevoked).ToListAsync(ct))
            {
                pat.IsRevoked = true;
            }

            await database.SaveChangesAsync(ct);
        }
    }
}
