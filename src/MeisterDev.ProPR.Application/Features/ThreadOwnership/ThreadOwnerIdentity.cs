// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ThreadOwnership;

/// <summary>
///     The account ProPR's comments appear under: the identity the connection's token authenticates as.
/// </summary>
/// <remarks>
///     Providers name an author differently, so both forms are carried and a provider populates whichever it
///     has. Azure DevOps names an author by identity GUID; GitHub, GitLab and Forgejo by login. No provider
///     persists this identity, so it is resolved through the same live handshake the adapter already performs
///     to reach the host, which is why it can be absent.
/// </remarks>
/// <param name="Id">Provider identity GUID, when the provider names authors that way.</param>
/// <param name="Login">Provider-native login or display name, when the provider names authors that way.</param>
public readonly record struct ThreadOwnerIdentity(Guid? Id = null, string? Login = null)
{
    /// <summary>No identity could be resolved, leaving provenance as the only evidence of ownership.</summary>
    public static ThreadOwnerIdentity None => default;

    /// <summary>Whether this identity is the author named by the supplied id or login.</summary>
    public bool Matches(Guid? authorId, string? authorLogin)
    {
        if (this.Id is { } identityId
            && identityId != Guid.Empty
            && authorId is { } candidateId
            && candidateId == identityId)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(this.Login)
               && !string.IsNullOrWhiteSpace(authorLogin)
               && string.Equals(this.Login, authorLogin, StringComparison.OrdinalIgnoreCase);
    }
}
