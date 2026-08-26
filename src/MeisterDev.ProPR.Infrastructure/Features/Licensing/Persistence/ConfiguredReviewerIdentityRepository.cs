// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Reads the configured reviewer identities of every client's connections on one host.
/// </summary>
/// <remarks>
///     The rows are the same ones the reviewer-trigger configuration writes, read across clients rather than
///     for one client and connection. The host comes from the connection the identity belongs to, because the
///     identity row records the provider family but not the host, and a provider-native identifier names one
///     account within one host.
/// </remarks>
public sealed class ConfiguredReviewerIdentityRepository(MeisterProPRDbContext dbContext)
    : IConfiguredReviewerIdentitySource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListExternalUserIdsAsync(
        ProviderHostRef host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        // The provider narrows the read in the database, from the identity row's own column; the host is
        // matched afterwards. A self-hosted Azure DevOps connection stores its collection path in the host URL
        // while a host reference carries the authority alone, so the two are compared by the rule the
        // connection lookup uses rather than for equality. The rows are one per configured connection, so the
        // set matched here is small.
        var candidates = await dbContext.ClientReviewerIdentities
            .AsNoTracking()
            .Where(identity => identity.Provider == host.Provider)
            .Select(identity => new
            {
                identity.ExternalUserId,
                ConnectionHostBaseUrl = identity.Connection!.HostBaseUrl,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates
            .Where(candidate => HostMatches(candidate.ConnectionHostBaseUrl, host.HostBaseUrl))
            .Select(candidate => candidate.ExternalUserId)
            .Distinct(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Whether a connection's stored host URL names the host the observation came from. One is a prefix of
    ///     the other when the connection carries a collection or instance path that a host authority does not.
    /// </summary>
    private static bool HostMatches(string connectionHostBaseUrl, string hostBaseUrl)
    {
        var connectionHost = connectionHostBaseUrl.Trim().TrimEnd('/');
        var observedHost = hostBaseUrl.Trim().TrimEnd('/');

        if (string.Equals(connectionHost, observedHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return connectionHost.StartsWith(observedHost + "/", StringComparison.OrdinalIgnoreCase)
               || observedHost.StartsWith(connectionHost + "/", StringComparison.OrdinalIgnoreCase);
    }
}
