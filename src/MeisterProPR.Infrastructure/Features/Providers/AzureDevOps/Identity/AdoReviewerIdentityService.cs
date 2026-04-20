// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using static MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

internal sealed class AdoReviewerIdentityService(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    IIdentityResolver identityResolver) : IReviewerIdentityService
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        var matchingScopes = (await ResolveOrganizationScopesAsync(
                connectionRepository,
                scopeRepository,
                clientId,
                host,
                ct))
            .Where(scope => scope.IsEnabled)
            .ToList();

        var identities = new Dictionary<string, ReviewerIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in matchingScopes)
        {
            var organizationUrl = NormalizeOrganizationUrl(scope.ScopePath);
            var resolvedIdentities = await identityResolver.ResolveAsync(organizationUrl, searchText, clientId, ct);
            foreach (var resolvedIdentity in resolvedIdentities)
            {
                identities.TryAdd(
                    resolvedIdentity.Id.ToString("D"),
                    new ReviewerIdentity(
                        host,
                        resolvedIdentity.Id.ToString("D"),
                        resolvedIdentity.DisplayName,
                        resolvedIdentity.DisplayName,
                        false));
            }
        }

        return identities.Values.ToList().AsReadOnly();
    }
}
