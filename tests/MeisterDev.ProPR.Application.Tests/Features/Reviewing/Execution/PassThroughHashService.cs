// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Stands in for BCrypt. The services under test decide what to hash and when to verify, not how, and a
///     real work factor would make every test that touches a credential pay for it.
/// </summary>
internal sealed class PassThroughHashService : IPasswordHashService
{
    public string Hash(string password)
    {
        return $"hashed:{password}";
    }

    public bool Verify(string password, string hash)
    {
        return string.Equals($"hashed:{password}", hash, StringComparison.Ordinal);
    }
}
