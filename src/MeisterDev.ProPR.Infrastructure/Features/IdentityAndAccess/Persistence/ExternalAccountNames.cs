// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>The usernames of the accounts that a sign-in through an external identity provider creates.</summary>
public static class ExternalAccountNames
{
    /// <summary>
    ///     Returns a username no account holds yet. It is derived from <paramref name="source" />, an email address or
    ///     the name of the account at the identity provider: the part before the at sign, in lower case, reduced to
    ///     letters, digits, dots, underscores and hyphens. When that name is taken, a number is appended.
    /// </summary>
    /// <param name="userRepository">The repository that answers whether a username is taken.</param>
    /// <param name="source">The email address or the name the username is derived from.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> CreateUniqueUsernameAsync(
        IUserRepository userRepository,
        string source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userRepository);
        ArgumentNullException.ThrowIfNull(source);

        var localPart = source.Split('@', 2)[0].Trim().ToLowerInvariant();
        var baseUsername = new string(
            localPart
                .Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-')
                .ToArray());

        if (string.IsNullOrWhiteSpace(baseUsername))
        {
            baseUsername = "user";
        }

        var candidate = baseUsername;
        var suffix = 1;
        while (await userRepository.GetByUsernameAsync(candidate, ct) is not null)
        {
            candidate = $"{baseUsername}{suffix}";
            suffix++;
        }

        return candidate;
    }
}
