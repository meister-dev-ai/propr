// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Evaluates saved review-source coordinates without credentials or provider requests.</summary>
public interface IReviewSourcePolicy
{
    /// <summary>Gets the provider family implemented by this policy.</summary>
    ScmProvider Provider { get; }

    /// <summary>Selects the source captured for namespace identity without consulting current configuration.</summary>
    string SelectCapturedSource(string scope, string? host);

    /// <summary>Normalizes native namespace coordinates and removes credentials, queries and fragments.</summary>
    string NormalizeNamespace(string source);

    /// <summary>Projects saved repository coordinates onto the existing comparison and lock key.</summary>
    string GetRepositoryIdentityKey(string repositoryId, string capturedRepositoryId, string project, string? projectPath, string? owner);

    /// <summary>Checks the deployment scope for an explicitly selected connection.</summary>
    bool IsSelectedScopeCompatible(string connectionHost, string scope);

    /// <summary>Checks native deployment paths after the caller validates the host origin.</summary>
    bool IsTargetScopeCompatible(string connectionHost, string scope);

    /// <summary>Checks saved source deployment compatibility for cached observations.</summary>
    bool IsCacheScopeCompatible(string connectionHost, string scope);

    /// <summary>Checks the provider scope accepted by repository discovery.</summary>
    bool IsDiscoveryScopeValid(string connectionOrigin, string scope);

    /// <summary>Selects the saved target scope from the connection and requested scope.</summary>
    string? SelectTargetScope(string connectionHost, string? requestedScope);

    /// <summary>Checks provider-specific canonical target scope requirements.</summary>
    bool IsTargetScopeCanonical(string? scope);

    /// <summary>Indicates whether target discovery requires saved organization coverage.</summary>
    bool RequiresOrganizationScope { get; }

    string ResolveGuidedScopePath(string? requestedPath, string configurationKind);
    ClientScmScopeDto ValidateGuidedSelectedScope(ClientScmScopeDto? scope);
    string GetGuidedFilterUnavailableMessage(string configurationKind, string displayName);

    /// <summary>Prepares supported native coordinates for the bounded review-target symbol query.</summary>
    ProCursorReviewContextDto? PrepareProCursorSymbolContext(
        RepositoryRef repository, string sourceBranch,
        int pullRequestNumber, int observationSequence);

    /// <summary>Gets the native verification prerequisite message for a missing saved scope.</summary>
    string MissingVerificationScopeMessage { get; }

    /// <summary>Prepares native mention coverage selection using saved metadata only.</summary>
    ReviewMentionScopeSelection PrepareMentionScopeSelection(string requestedScope);

    /// <summary>Projects a recorded canonical repository reference without reassigning its current host family.</summary>
    RepositoryRef ProjectRecordedCanonicalRepository(RepositoryRef repository, string? canonicalId, string configuredProject);

    /// <summary>Returns native repository aliases accepted by saved webhook filters.</summary>
    IReadOnlyList<string> GetWebhookRepositoryAliases(RepositoryRef repository);

    /// <summary>Prepares a local saved-scope projection when the native deployment URL changes.</summary>
    Func<string, string?>? CreateScopeRepointing(string previousHostBaseUrl, string newHostBaseUrl);

    /// <summary>Matches a saved organization scope to target coverage.</summary>
    bool MatchesOrganizationScope(ClientScmScopeDto scope, string targetScope);

    /// <summary>Matches an enabled scope's native coordinates to the target.</summary>
    bool MatchesGrant(CrawlConfigurationDto target, ClientScmScopeDto scope);

    /// <summary>Selects the scope passed to provider repository discovery.</summary>
    string GetRepositoryDiscoveryScope(string targetScope, string project);

    /// <summary>Matches native repository identity or supported path aliases to a saved target.</summary>
    bool MatchesRepository(CrawlConfigurationDto target, RepositoryRef repository);

    /// <summary>Constructs native repository coordinates from saved target fields.</summary>
    RepositoryRef CreateRepository(string connectionHost, string repositoryId, string project, string name);

    /// <summary>Projects captured coverage names without requiring an unqualified name.</summary>
    RepositoryRef CreateCapturedRepository(string connectionHost, string repositoryId, string project, string name);

    /// <summary>Matches a captured native repository identifier and its supported saved-name aliases.</summary>
    bool MatchesCapturedRepository(
        string canonicalId, string savedName, string project, string requestedId, string requestedPath, bool normalizeWhitespace = false);

    /// <summary>Projects captured mention coordinates using the existing native name grammar.</summary>
    string ResolveMentionRepositoryPath(string project, string repositoryId, string? claimedName, string pullRequestName);

    /// <summary>Matches address-derived scope coordinates against saved coverage.</summary>
    bool MatchesAddressScope(string project, string scopePath, string requestedScope);

    /// <summary>Matches a saved repository name and its supported basename alias.</summary>
    bool MatchesRepositoryName(string? savedName, string requestedName);

    /// <summary>Selects a discovered repository by captured identity, path or basename, in that order.</summary>
    RepositoryRef? FindRepositoryByCapturedIdentity(IReadOnlyList<RepositoryRef> repositories, string identity);

    /// <summary>Selects a discovered repository by its address-derived name.</summary>
    RepositoryRef? FindRepositoryByAddressName(IReadOnlyList<RepositoryRef> repositories, string name);

    /// <summary>Returns normalized cache identity fields without changing their serialized representation.</summary>
    ReviewSourceCacheCoordinates GetCacheCoordinates(string scope, string project);
}

/// <summary>Contains the provider-normalized fields used in repository metadata cache identity.</summary>
public sealed record ReviewSourceCacheCoordinates(string Authority, string ScopePath, string ProjectKey);

/// <summary>Selects saved scope coverage or a connection host using the native coordinate predicate.</summary>
public sealed record ReviewMentionScopeSelection(string? ScopeType, Func<string, bool> MatchesScope);
