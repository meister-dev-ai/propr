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

    public ConnectionDiscoveryDescriptor Descriptor => new(
        this.Provider, "Organization", "Project",
        [new(ProCursorSourceKind.Repository, "Repository"), new(ProCursorSourceKind.AdoWiki, "Wiki")], true, true);

    public ConnectionDiscoveryCoordinates GetConfigurationCoordinates(ConnectionDiscoveryContext context, ConnectionDiscoveryScope scope, string? projectId) =>
        scope.SavedScopeId.HasValue && !string.IsNullOrWhiteSpace(projectId)
            ? new(scope.SavedScopeId, scope.ScopeKey, projectId)
            : throw new InvalidOperationException("Select a saved organization scope and project.");

    public async Task<IReadOnlyList<ConnectionDiscoveryScope>> ListScopesAsync(ConnectionDiscoveryContext context, CancellationToken ct = default)
    {
        await this.ValidateConnectionAsync(context, ct).ConfigureAwait(false);
        var scopes = await scopeRepository.GetByConnectionIdAsync(context.ClientId, context.ConnectionId, ct).ConfigureAwait(false);
        return scopes.Where(scope => scope.ClientId == context.ClientId && scope.ConnectionId == context.ConnectionId &&
                                     scope.IsEnabled && scope.ScopeType.Equals("organization", StringComparison.OrdinalIgnoreCase))
            .Select(scope => new ConnectionDiscoveryScope(scope.ScopePath, scope.DisplayName, scope.Id)).ToList();
    }

    public async Task<IReadOnlyList<ConnectionDiscoverySource>> ListSourcesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, ProCursorSourceKind sourceKind, CancellationToken ct = default)
    {
        await this.ValidateConnectionAsync(context, ct).ConfigureAwait(false);
        var scope = (await scopeRepository.GetByConnectionIdAsync(context.ClientId, context.ConnectionId, ct).ConfigureAwait(false))
                    .SingleOrDefault(candidate => candidate.ClientId == context.ClientId && candidate.ConnectionId == context.ConnectionId &&
                                                  candidate.IsEnabled && candidate.ScopeType.Equals("organization", StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(candidate.ScopePath, scopeKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The selected scope does not belong to this connection.");
        if (string.IsNullOrWhiteSpace(projectId) || !this.Descriptor.SourceKinds.Any(kind => kind.Kind == sourceKind))
        {
            throw new InvalidOperationException("Select a supported source kind and project.");
        }

        var projects = await discoveryService.ListProjectOptionsAsync(context.ClientId, scope.Id, ct, context.ConnectionId).ConfigureAwait(false);
        if (!projects.Any(project => project.ProjectId == projectId))
        {
            throw new InvalidOperationException("The selected project is not available in this scope.");
        }

        var sources = await discoveryService.ListSourceOptionsAsync(context.ClientId, scope.Id, projectId, sourceKind, ct, context.ConnectionId)
            .ConfigureAwait(false);
        return sources.Select(source => new ConnectionDiscoverySource(
            scope.Id, scope.ScopePath, projectId, source.CanonicalSourceRef.Value, sourceKind,
            source.CanonicalSourceRef, source.DisplayName, source.DefaultBranch)).ToList();
    }

    private async Task ValidateConnectionAsync(ConnectionDiscoveryContext context, CancellationToken ct)
    {
        EnsureAzureDevOps(context.Host);
        var connection = await connectionRepository.GetOperationalConnectionByIdAsync(context.ClientId, context.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null || !connection.IsActive || connection.Id != context.ConnectionId || connection.ClientId != context.ClientId ||
            connection.ProviderFamily != this.Provider ||
            !string.Equals(
                new ProviderHostRef(this.Provider, connection.HostBaseUrl).HostBaseUrl,
                context.Host.HostBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected connection is not available for this client and host.");
        }
    }

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

        var projects = await discoveryService.ListProjectOptionsAsync(clientId, scope.Id, ct);
        var repositories = new List<RepositoryRef>();

        foreach (var project in projects)
        {
            var sources = await discoveryService.ListSourceOptionsAsync(
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
                    project.ProjectId,
                    source.DisplayName,
                    project.ProjectName)));
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
