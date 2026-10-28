// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     Creates personal access tokens for the authenticated account's token management endpoint. ProPR stores token
///     hashes and returns the plaintext token once.
/// </summary>
internal static class PersonalAccessTokens
{
    /// <summary>Returns a new token of an account and the record that stores its hashes.</summary>
    /// <param name="userId">The account the token belongs to.</param>
    /// <param name="label">The name of the token in the list of tokens of the account.</param>
    /// <param name="expiresAt">When the token expires, or <see langword="null" /> for a token without expiry.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested expiry is not in the future.</exception>
    public static (UserPat Record, string Token) Issue(Guid userId, string label, DateTimeOffset? expiresAt)
    {
        var createdAt = DateTimeOffset.UtcNow;
        if (expiresAt is not null && expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be in the future.");
        }

        var token = "mpr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var record = new UserPat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = BCrypt.Net.BCrypt.HashPassword(token),
            TokenLookupHash = PatTokenLookupHash.Compute(token),
            Label = label,
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
        };

        return (record, token);
    }
}
