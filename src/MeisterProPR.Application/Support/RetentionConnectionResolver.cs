// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Support;

/// <summary>
///     Resolves the client SCM connection that owns a pull request from its provider family and host,
///     matching on the host authority. This is the same resolution used when retained data is ingested,
///     so reads and writes agree on the owning connection.
/// </summary>
public static class RetentionConnectionResolver
{
    /// <summary>
    ///     Picks the active connection whose provider family matches <paramref name="provider" /> and whose
    ///     stored host base URL shares the authority of <paramref name="hostBaseUrl" />. When several
    ///     connections share an authority the most specific (longest stored host) wins. Returns null when
    ///     no active connection matches.
    /// </summary>
    /// <param name="connections">Candidate connections, typically all connections for a client.</param>
    /// <param name="provider">The provider family the pull request belongs to.</param>
    /// <param name="hostBaseUrl">The pull request host, normalized to an authority (scheme://host[:port]).</param>
    /// <returns>The matching connection, or null.</returns>
    public static ClientScmConnectionDto? Resolve(
        IEnumerable<ClientScmConnectionDto> connections,
        ScmProvider provider,
        string hostBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(connections);

        return connections
            .Where(connection => connection.IsActive
                                 && connection.ProviderFamily == provider
                                 && HostMatchesAuthority(connection.HostBaseUrl, hostBaseUrl))
            // Prefer the most specific host match when several connections share an authority.
            .OrderByDescending(connection => connection.HostBaseUrl.Length)
            .FirstOrDefault();
    }

    private static bool HostMatchesAuthority(string connectionHostBaseUrl, string hostAuthority)
    {
        // The job host is normalized to an authority (scheme://host[:port]); a connection's stored host
        // base URL may carry a path (e.g. an Azure DevOps organization URL). Match on the authority.
        if (!Uri.TryCreate(connectionHostBaseUrl.Trim(), UriKind.Absolute, out var connectionUri))
        {
            return string.Equals(
                connectionHostBaseUrl.Trim().TrimEnd('/'),
                hostAuthority.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        var connectionAuthority = connectionUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return string.Equals(connectionAuthority, hostAuthority.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
