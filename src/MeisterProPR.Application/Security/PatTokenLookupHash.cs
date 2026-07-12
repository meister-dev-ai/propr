// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;

namespace MeisterProPR.Application.Security;

/// <summary>
///     Computes the deterministic lookup hash for a Personal Access Token. This is an indexed lookup key that
///     narrows a token to a single candidate row; the authoritative credential check remains the salted BCrypt
///     verification against that row. Issuance and lookup MUST use this one implementation so the values match.
/// </summary>
public static class PatTokenLookupHash
{
    /// <summary>Returns the uppercase hex SHA-256 of the raw token.</summary>
    public static string Compute(string rawToken)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }
}
