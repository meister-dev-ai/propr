// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using Microsoft.VisualStudio.Services.Identity;
using Microsoft.VisualStudio.Services.Identity.Client;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

/// <summary>
///     ADO-backed implementation of <see cref="IIdentityResolver" /> that resolves identities
///     through the authenticated Azure DevOps client connection.
/// </summary>
public sealed class AdoIdentityResolver(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository)
    : IIdentityResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ResolvedIdentity>> ResolveAsync(
        string organizationUrl,
        string displayName,
        Guid clientId,
        Guid? connectionId = null,
        CancellationToken ct = default)
    {
        var credentials = connectionId.HasValue
            ? AdoProviderAdapterHelpers.ToAdoCredentials(await connectionRepository.GetOperationalConnectionByIdAsync(clientId, connectionId.Value, ct))
            : await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
                connectionRepository,
                clientId,
                organizationUrl,
                ct);
        AdoProviderAdapterHelpers.EnsureRuntimeCredentialsAvailable(organizationUrl, credentials);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        var client = await connection.GetClientAsync<IdentityHttpClient>(ct);
        var identities = await client.ReadIdentitiesAsync(
            IdentitySearchFilter.General,
            displayName,
            ReadIdentitiesOptions.None,
            QueryMembership.None,
            null,
            null,
            ct);

        return identities?
                   .Where(identity => identity.Id != Guid.Empty)
                   .Select(identity => new ResolvedIdentity(
                       identity.Id,
                       !string.IsNullOrWhiteSpace(identity.DisplayName)
                           ? identity.DisplayName!
                           : identity.ProviderDisplayName ?? identity.Id.ToString("D")))
                   .ToList()
               ?? [];
    }
}
