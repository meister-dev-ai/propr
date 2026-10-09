// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ThreadOwnership;

/// <summary>
///     The account ProPR's comments appear under: the identity the connection's token authenticates as.
/// </summary>
/// <remarks>
///     Provider capabilities populate a normalized identity GUID, login, or both. This identity is resolved
///     through the existing authenticated provider operation and can be absent when that operation fails.
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
