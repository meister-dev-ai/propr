// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Shared issuer for the application's JWT plus refresh-token session contract.</summary>
public interface ISessionFactory
{
    Task<IssuedSession> CreateAsync(AppUser user, CancellationToken ct = default);
}

/// <summary>JWT plus refresh-token session payload returned by authentication endpoints.</summary>
public sealed record IssuedSession(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn = 900,
    string TokenType = "Bearer");
