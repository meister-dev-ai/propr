// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using static MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;

internal sealed class AdoRepositoryDiscoveryProvider(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    IProviderAdminDiscoveryService discoveryService) : IRepositoryDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<IReadOnlyList<string>> ListScopesAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(host);

        var scopes = await ResolveOrganizationScopesAsync(connectionRepository, scopeRepository, clientId, host, ct);
        return scopes
            .Select(scope => NormalizeOrganizationUrl(scope.ScopePath))
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(
        Guid clientId,
        ProviderHostRef host,
        string scopePath,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(host);

        var scope = await this.ResolveScopeAsync(clientId, scopePath, ct);
        if (scope is null || !scope.IsEnabled)
        {
            return [];
        }

        var projects = await discoveryService.ListProjectsAsync(clientId, scope.Id, ct);
        var repositories = new List<RepositoryRef>();

        foreach (var project in projects)
        {
            var sources = await discoveryService.ListSourcesAsync(
                clientId,
                scope.Id,
                project.ProjectId,
                ProCursorSourceKind.Repository,
                ct);

            repositories.AddRange(
                sources.Select(source => new RepositoryRef(
                    host,
                    source.CanonicalSourceRef.Value,
                    project.ProjectId,
                    project.ProjectId)));
        }

        return repositories;
    }

    private async Task<ClientScmScopeDto?> ResolveScopeAsync(Guid clientId, string scopePath, CancellationToken ct)
    {
        var normalizedScopePath = scopePath.Trim().TrimEnd('/');
        var scopes = await ResolveOrganizationScopesAsync(
            connectionRepository,
            scopeRepository,
            clientId,
            new ProviderHostRef(ScmProvider.AzureDevOps, normalizedScopePath),
            ct);

        return scopes.FirstOrDefault(scope =>
            string.Equals(
                NormalizeOrganizationUrl(scope.ScopePath),
                normalizedScopePath,
                StringComparison.OrdinalIgnoreCase));
    }
}
