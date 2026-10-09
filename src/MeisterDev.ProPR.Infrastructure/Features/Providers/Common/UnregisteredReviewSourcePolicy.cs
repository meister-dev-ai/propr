// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Projects historical unknown-provider identities without authorizing provider execution.</summary>
internal sealed class UnregisteredReviewSourcePolicy(ScmProvider provider) : ReviewSourcePolicyBase, IReviewSourcePolicy
{
    public override ScmProvider Provider => provider;
    public bool RequiresOrganizationScope => false;
    public bool IsSelectedScopeCompatible(string connectionHost, string scope) => false;
    public bool IsTargetScopeCompatible(string connectionHost, string scope) => false;
    public bool IsCacheScopeCompatible(string connectionHost, string scope) => false;
    public bool IsDiscoveryScopeValid(string connectionOrigin, string scope) => false;
    public string? SelectTargetScope(string connectionHost, string? requestedScope) => null;
    public bool IsTargetScopeCanonical(string? scope) => false;
    public bool MatchesOrganizationScope(ClientScmScopeDto scope, string targetScope) => false;
    public bool MatchesGrant(CrawlConfigurationDto target, ClientScmScopeDto scope) => false;
    public string GetRepositoryDiscoveryScope(string targetScope, string project) => project;
    public bool MatchesRepository(CrawlConfigurationDto target, RepositoryRef repository) => false;

    public RepositoryRef CreateRepository(string connectionHost, string repositoryId, string project, string name) =>
        new(new(provider, connectionHost), repositoryId, project, name.Contains('/') ? name : $"{project}/{name}", name);

    public ReviewSourceCacheCoordinates GetCacheCoordinates(string scope, string project) =>
        new(new Uri(scope).GetLeftPart(UriPartial.Authority).ToLowerInvariant(), new Uri(scope).AbsolutePath.TrimEnd('/'), project);
}
