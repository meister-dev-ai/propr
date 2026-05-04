// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>Creates the standard JWT plus refresh-token session payload used by auth endpoints.</summary>
public sealed class SessionFactory(
    IJwtTokenService jwtTokenService,
    IRefreshTokenRepository refreshTokenRepository) : ISessionFactory
{
    public async Task<IssuedSession> CreateAsync(AppUser user, CancellationToken ct = default)
    {
        var accessToken = jwtTokenService.GenerateAccessToken(user);
        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = ComputeSha256(rawRefreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await refreshTokenRepository.AddAsync(refreshToken, ct);

        return new IssuedSession(accessToken, rawRefreshToken);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
