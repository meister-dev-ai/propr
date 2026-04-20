// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>Hashes and verifies passwords using BCrypt.</summary>
public interface IPasswordHashService
{
    /// <summary>Returns a BCrypt hash of <paramref name="password" />.</summary>
    string Hash(string password);

    /// <summary>Returns true if <paramref name="password" /> matches <paramref name="hash" />.</summary>
    bool Verify(string password, string hash);
}
