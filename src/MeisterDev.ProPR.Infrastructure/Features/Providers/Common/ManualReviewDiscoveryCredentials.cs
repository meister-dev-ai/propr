// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal static class ManualReviewDiscoveryCredentials
{
    public static async Task<ClientScmConnectionCredentialDto> ResolveAsync(
        IClientScmConnectionRepository connections, Guid clientId, ProviderHostRef host,
        ReviewDiscoveryContext context, Func<string, string, bool> isScopeCompatible, CancellationToken ct)
    {
        var connection = await connections.GetOperationalConnectionByIdAsync(clientId, context.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null || connection.Id != context.ConnectionId || connection.ClientId != clientId)
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        if (!connection.IsActive || connection.ProviderFamily != host.Provider)
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        if (!TryUrl(connection.HostBaseUrl, out var connectionUrl) || !TryUrl(context.ProviderScopePath, out var scopeUrl))
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        var connectionAuthority = connectionUrl.GetLeftPart(UriPartial.Authority);
        var scopeAuthority = scopeUrl.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(connectionAuthority, scopeAuthority, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scopeAuthority, host.HostBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        if (!isScopeCompatible(connection.HostBaseUrl, context.ProviderScopePath))
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        return connection;
    }

    private static bool TryUrl(string value, out Uri url)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" && string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment))
        {
            url = parsed;
            return true;
        }

        url = null!;
        return false;
    }
}
