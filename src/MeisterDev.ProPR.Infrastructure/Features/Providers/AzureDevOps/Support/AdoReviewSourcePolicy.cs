// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>Evaluates AzureDevOps source coordinates using saved non-secret configuration.</summary>
internal sealed class AdoReviewSourcePolicy : ReviewSourcePolicyBase, IReviewSourcePolicy
{
    public override ScmProvider Provider => ScmProvider.AzureDevOps;

    public override ProCursorReviewContextDto PrepareProCursorSymbolContext(
        RepositoryRef repository, string sourceBranch, int pullRequestNumber, int observationSequence) =>
        new(repository.ExternalRepositoryId, sourceBranch, pullRequestNumber, observationSequence);

    public override string ResolveGuidedScopePath(string? requestedPath, string configurationKind) =>
        string.IsNullOrWhiteSpace(requestedPath)
            ? throw new InvalidOperationException("ProviderScopePath is required when OrganizationScopeId is not provided.")
            : requestedPath.Trim();

    public override ClientScmScopeDto ValidateGuidedSelectedScope(ClientScmScopeDto? scope)
    {
        if (scope is null)
        {
            throw new InvalidOperationException("The selected Azure DevOps organization is no longer available for this client.");
        }

        if (!scope.IsEnabled)
        {
            throw new InvalidOperationException("The selected Azure DevOps organization is disabled.");
        }

        return scope;
    }

    public override string GetGuidedFilterUnavailableMessage(string configurationKind, string displayName) =>
        $"The selected {configurationKind} filter '{displayName}' is no longer available in Azure DevOps.";

    public bool RequiresOrganizationScope => true;

    public override RepositoryRef ProjectRecordedCanonicalRepository(RepositoryRef repository, string? canonicalId, string configuredProject)
    {
        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            return repository;
        }

        var carriesExplicitProjectIdentity =
            !string.Equals(repository.OwnerOrNamespace, repository.ExternalRepositoryId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.ProjectPath, repository.ExternalRepositoryId, StringComparison.OrdinalIgnoreCase);
        var ownerOrNamespace = carriesExplicitProjectIdentity ? repository.OwnerOrNamespace : configuredProject;
        var projectPath = carriesExplicitProjectIdentity ? repository.ProjectPath : configuredProject;
        if (string.Equals(repository.ExternalRepositoryId, canonicalId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repository.OwnerOrNamespace, ownerOrNamespace, StringComparison.Ordinal)
            && string.Equals(repository.ProjectPath, projectPath, StringComparison.Ordinal))
        {
            return repository;
        }

        return new RepositoryRef(repository.Host, canonicalId, ownerOrNamespace, projectPath);
    }

    public override ReviewMentionScopeSelection PrepareMentionScopeSelection(string requestedScope) =>
        base.PrepareMentionScopeSelection(requestedScope) with { ScopeType = "organization" };

    public override string MissingVerificationScopeMessage => "Add an enabled organization scope before verifying Azure DevOps provider connections.";

    public override Func<string, string?>? CreateScopeRepointing(string previousHostBaseUrl, string newHostBaseUrl)
    {
        if (string.Equals(previousHostBaseUrl, newHostBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return scopePath =>
        {
            if (!Uri.TryCreate(scopePath.Trim(), UriKind.Absolute, out _))
            {
                return null;
            }

            var scope = AdoStoredDeploymentCoordinates.Normalize(scopePath);
            var previous = AdoStoredDeploymentCoordinates.Normalize(previousHostBaseUrl);
            var current = AdoStoredDeploymentCoordinates.Normalize(newHostBaseUrl);
            if (!string.Equals(scope, previous, StringComparison.OrdinalIgnoreCase)
                && !scope.StartsWith(previous + "/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return (current + scope[previous.Length..]).TrimEnd('/');
        };
    }

    public override string SelectCapturedSource(string scope, string? host) => scope;

    public override string NormalizeNamespace(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return IsHostedHost(uri.Host)
            ? base.NormalizeNamespace(source)
            : uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
    }

    public override string GetRepositoryIdentityKey(string repositoryId, string capturedRepositoryId, string project, string? projectPath, string? owner) =>
        repositoryId;

    internal static bool IsHostedHost(string host) =>
        string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    public bool IsSelectedScopeCompatible(string connectionHost, string scope) =>
        ReviewSourceUris.TryRead(connectionHost, out var connection) && ReviewSourceUris.TryRead(scope, out var selected) &&
        ReviewSourceUris.SameAuthority(connection, selected) &&
        selected.AbsolutePath != "/" && (connection.AbsolutePath == "/" ||
                                         string.Equals(
                                             connection.AbsolutePath.TrimEnd('/'), selected.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public bool IsTargetScopeCompatible(string connectionHost, string scope) =>
        true;

    public bool IsCacheScopeCompatible(string connectionHost, string scope) =>
        ReviewSourceUris.TryRead(connectionHost, out var connection, false) && ReviewSourceUris.TryRead(scope, out var selected, false) &&
        ReviewSourceUris.SameAuthority(connection, selected) &&
        (connection.AbsolutePath == "/" ||
         string.Equals(connection.AbsolutePath.TrimEnd('/'), selected.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public bool IsDiscoveryScopeValid(string connectionOrigin, string scope) =>
        ReviewSourceUris.TryRead(scope, out var selected, false) &&
        string.Equals(selected.GetLeftPart(UriPartial.Authority), connectionOrigin, StringComparison.OrdinalIgnoreCase);

    public string? SelectTargetScope(string connectionHost, string? requestedScope) =>
        requestedScope?.Trim().TrimEnd('/');

    public bool IsTargetScopeCanonical(string? scope) =>
        Uri.TryCreate(scope, UriKind.Absolute, out var selected) &&
        string.IsNullOrEmpty(selected.Query) && string.IsNullOrEmpty(selected.Fragment) &&
        string.Equals(selected.GetLeftPart(UriPartial.Path).TrimEnd('/'), scope, StringComparison.Ordinal);

    public bool MatchesOrganizationScope(ClientScmScopeDto scope, string targetScope) =>
        string.Equals(scope.ScopeType, "organization", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(scope.ScopePath.TrimEnd('/'), targetScope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public bool MatchesGrant(CrawlConfigurationDto target, ClientScmScopeDto scope) =>
        scope.ScopeType == "organization" &&
        scope.ScopePath.TrimEnd('/').Equals(target.ProviderScopePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
        scope.ScopeType == "repository" && scope.ExternalScopeId == target.RepoFilters[0].CanonicalSourceRef!.Value &&
        scope.ScopePath.Trim('/').Equals(
            $"{target.ProviderProjectKey.TrimEnd('/')}/{target.RepoFilters[0].RepositoryName}", StringComparison.OrdinalIgnoreCase);

    public string GetRepositoryDiscoveryScope(string targetScope, string project) => targetScope;

    public bool MatchesRepository(CrawlConfigurationDto target, RepositoryRef repository)
    {
        var filter = target.RepoFilters[0];
        return string.Equals(filter.CanonicalSourceRef!.Value, repository.ExternalRepositoryId, StringComparison.OrdinalIgnoreCase);
    }

    public RepositoryRef CreateRepository(string connectionHost, string repositoryId, string project, string name) =>
        new(new(Provider, connectionHost), repositoryId, project, project, name);

    public override RepositoryRef CreateCapturedRepository(string connectionHost, string repositoryId, string project, string name) =>
        new(new(Provider, connectionHost), repositoryId, project, project, LastSegment(name));

    public override bool MatchesCapturedRepository(
        string canonicalId, string savedName, string project, string requestedId, string requestedPath, bool normalizeWhitespace = false) =>
        string.Equals(
            normalizeWhitespace ? canonicalId.Trim() : canonicalId,
            normalizeWhitespace ? requestedId.Trim() : requestedId, StringComparison.OrdinalIgnoreCase);

    public override string ResolveMentionRepositoryPath(string project, string repositoryId, string? claimedName, string pullRequestName) =>
        project;

    public ReviewSourceCacheCoordinates GetCacheCoordinates(string scope, string project)
    {
        var uri = new Uri(scope);
        return new(
            uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
            uri.AbsolutePath.TrimEnd('/').ToLowerInvariant(), project.ToLowerInvariant());
    }
}
