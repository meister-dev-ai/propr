// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;

/// <summary>Evaluates GitLab source coordinates using saved non-secret configuration.</summary>
internal sealed class GitLabReviewSourcePolicy : ReviewSourcePolicyBase, IReviewSourcePolicy
{
    public override ScmProvider Provider => ScmProvider.GitLab;
    public bool RequiresOrganizationScope => false;

    public bool IsSelectedScopeCompatible(string connectionHost, string scope) =>
        ReviewSourceUris.TryRead(connectionHost, out var connection) && ReviewSourceUris.TryRead(scope, out var selected) &&
        ReviewSourceUris.SameAuthority(connection, selected) &&
        string.Equals(connection.AbsolutePath.TrimEnd('/'), selected.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public bool IsTargetScopeCompatible(string connectionHost, string scope) =>
        ReviewSourceUris.TryRead(connectionHost, out var connection) && ReviewSourceUris.TryRead(scope, out var selected) &&
        ReviewSourceUris.SameAuthority(connection, selected) &&
        string.Equals(connection.AbsolutePath.TrimEnd('/'), selected.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public bool IsCacheScopeCompatible(string connectionHost, string scope) =>
        ReviewSourceUris.TryRead(connectionHost, out var connection, false) && ReviewSourceUris.TryRead(scope, out var selected, false) &&
        ReviewSourceUris.SameAuthority(connection, selected) &&
        string.Equals(connection.AbsolutePath.TrimEnd('/'), selected.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public bool IsDiscoveryScopeValid(string connectionOrigin, string scope) =>
        !Uri.TryCreate(scope, UriKind.Absolute, out _) && !scope.StartsWith('/');

    public string? SelectTargetScope(string connectionHost, string? requestedScope) =>
        connectionHost.TrimEnd('/');

    public bool IsTargetScopeCanonical(string? scope) =>
        true;

    public bool MatchesOrganizationScope(ClientScmScopeDto scope, string targetScope) =>
        false;

    public bool MatchesGrant(CrawlConfigurationDto target, ClientScmScopeDto scope) =>
        scope.ScopeType == "repository" && scope.ExternalScopeId == target.RepoFilters[0].CanonicalSourceRef!.Value &&
        scope.ScopePath.Trim('/').Equals(
            $"{target.ProviderProjectKey.TrimEnd('/')}/{target.RepoFilters[0].RepositoryName}", StringComparison.OrdinalIgnoreCase);

    public string GetRepositoryDiscoveryScope(string targetScope, string project) => project;

    public bool MatchesRepository(CrawlConfigurationDto target, RepositoryRef repository)
    {
        var filter = target.RepoFilters[0];
        if (string.Equals(filter.CanonicalSourceRef!.Value, repository.ExternalRepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = filter.RepositoryName.Contains('/', StringComparison.Ordinal)
            ? filter.RepositoryName
            : $"{target.ProviderProjectKey.TrimEnd('/')}/{filter.RepositoryName}";
        return string.Equals(path, repository.ProjectPath, StringComparison.OrdinalIgnoreCase);
    }

    public RepositoryRef CreateRepository(string connectionHost, string repositoryId, string project, string name) =>
        new(new(Provider, connectionHost), repositoryId, project, $"{project.TrimEnd('/')}/{name}", name);

    public ReviewSourceCacheCoordinates GetCacheCoordinates(string scope, string project)
    {
        var uri = new Uri(scope);
        return new(
            uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
            uri.AbsolutePath.TrimEnd('/'), project);
    }
}
