// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Shared issuer for the application's JWT plus refresh-token session contract.</summary>
public interface ISessionFactory
{
    /// <summary>
    ///     Creates a JWT plus refresh-token session for the supplied user.
    /// </summary>
    /// <param name="user">Authenticated user receiving the session.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The issued session payload.</returns>
    Task<IssuedSession> CreateAsync(AppUser user, CancellationToken ct = default);
}

/// <summary>JWT plus refresh-token session payload returned by authentication endpoints.</summary>
public sealed record IssuedSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    int ExpiresIn = 900,
    string TokenType = "Bearer")
{
    /// <summary>Renders the session without either token; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(IssuedSession)} {{ AccessToken = {SecretSafeRendering.Elide(this.AccessToken)}, "
               + $"RefreshToken = {SecretSafeRendering.Elide(this.RefreshToken)}, "
               + $"RefreshTokenExpiresAt = {this.RefreshTokenExpiresAt:O}, ExpiresIn = {this.ExpiresIn}, "
               + $"TokenType = {this.TokenType} }}";
    }
}
