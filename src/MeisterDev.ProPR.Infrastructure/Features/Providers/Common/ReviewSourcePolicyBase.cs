// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Provides generic URI sanitation and saved repository path projection.</summary>
internal abstract class ReviewSourcePolicyBase
{
    public abstract ScmProvider Provider { get; }

    public virtual ProCursorReviewContextDto? PrepareProCursorSymbolContext(
        RepositoryRef repository, string sourceBranch, int pullRequestNumber, int observationSequence) => null;

    public virtual RepositoryRef ProjectRecordedCanonicalRepository(RepositoryRef repository, string? canonicalId, string configuredProject) => repository;

    public virtual IReadOnlyList<string> GetWebhookRepositoryAliases(RepositoryRef repository) =>
        new[]
            {
                repository.ExternalRepositoryId, repository.ProjectPath,
                repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();

    public virtual string MissingVerificationScopeMessage => "Add an enabled provider scope before verifying provider connections.";

    public virtual string ResolveGuidedScopePath(string? requestedPath, string configurationKind) =>
        string.IsNullOrWhiteSpace(requestedPath)
            ? throw new InvalidOperationException(AdoConfigurationCompatibilityMessages.UnscopedConfigurationPathRequired(configurationKind))
            : requestedPath.Trim();

    public virtual ClientScmScopeDto ValidateGuidedSelectedScope(ClientScmScopeDto? scope) =>
        scope ?? throw new InvalidOperationException("The selected provider scope is no longer available for this client.");

    public virtual string GetGuidedFilterUnavailableMessage(string configurationKind, string displayName) =>
        $"The selected {configurationKind} filter '{displayName}' is no longer available in the provider.";

    public virtual ReviewMentionScopeSelection PrepareMentionScopeSelection(string requestedScope) =>
        new(null, stored => string.Equals(NormalizeMentionScope(stored), NormalizeMentionScope(requestedScope), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeMentionScope(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');

    public virtual Func<string, string?>? CreateScopeRepointing(string previousHostBaseUrl, string newHostBaseUrl) => null;

    public virtual string SelectCapturedSource(string scope, string? host) => host ?? scope;

    public virtual string NormalizeNamespace(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out _)
            ? new Uri(new ProviderHostRef(this.Provider, source).HostBaseUrl)
                .GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/')
            : string.Empty;

    public virtual string GetRepositoryIdentityKey(string repositoryId, string capturedRepositoryId, string project, string? projectPath, string? owner)
    {
        var path = string.IsNullOrWhiteSpace(projectPath) ? repositoryId : projectPath;
        if (repositoryId.Contains('/', StringComparison.Ordinal) || path.Contains('/', StringComparison.Ordinal))
        {
            return path;
        }

        var repositoryOwner = string.IsNullOrWhiteSpace(owner) ? project : owner;
        return string.Equals(repositoryId, capturedRepositoryId, StringComparison.OrdinalIgnoreCase)
            ? $"{repositoryOwner}/{repositoryId}"
            : repositoryId;
    }

    public virtual RepositoryRef CreateCapturedRepository(string connectionHost, string repositoryId, string project, string name) =>
        new(
            new(this.Provider, connectionHost), repositoryId, project,
            name.Contains('/', StringComparison.Ordinal) ? name : $"{project}/{name}", LastSegment(name));

    public virtual bool MatchesCapturedRepository(
        string canonicalId, string savedName, string project, string requestedId, string requestedPath, bool normalizeWhitespace = false)
    {
        var path = savedName.Contains('/', StringComparison.Ordinal) ? savedName : $"{project.TrimEnd('/')}/{savedName}";
        return string.Equals(
                   normalizeWhitespace ? canonicalId.Trim() : canonicalId,
                   normalizeWhitespace ? requestedId.Trim() : requestedId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   normalizeWhitespace ? path.Trim() : path,
                   normalizeWhitespace ? requestedPath.Trim() : requestedPath, StringComparison.OrdinalIgnoreCase);
    }

    public virtual string ResolveMentionRepositoryPath(string project, string repositoryId, string? claimedName, string pullRequestName)
    {
        if (LooksLikeOwnerAndName(claimedName))
        {
            return claimedName!.Trim();
        }

        return LooksLikeOwnerAndName(pullRequestName) ? pullRequestName : repositoryId;
    }

    public bool MatchesAddressScope(string project, string scopePath, string requestedScope)
    {
        if (requestedScope.Length == 0 || string.Equals(project, requestedScope, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = Uri.TryCreate(scopePath?.Trim(), UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath).Trim('/')
            : string.Empty;
        return string.Equals(path, requestedScope, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith('/' + requestedScope, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesRepositoryName(string? savedName, string requestedName) =>
        string.Equals(savedName, requestedName.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(LastSegment(savedName), requestedName.Trim(), StringComparison.OrdinalIgnoreCase);

    public RepositoryRef? FindRepositoryByCapturedIdentity(IReadOnlyList<RepositoryRef> repositories, string identity) =>
        repositories.FirstOrDefault(repository => string.Equals(repository.ExternalRepositoryId, identity, StringComparison.OrdinalIgnoreCase))
        ?? repositories.FirstOrDefault(repository => string.Equals(repository.ProjectPath, identity, StringComparison.OrdinalIgnoreCase))
        ?? repositories.FirstOrDefault(repository => string.Equals(LastSegment(repository.ProjectPath), identity, StringComparison.OrdinalIgnoreCase));

    public RepositoryRef? FindRepositoryByAddressName(IReadOnlyList<RepositoryRef> repositories, string name) =>
        repositories.FirstOrDefault(repository =>
            string.Equals(repository.RepositoryName, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastSegment(repository.ProjectPath), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeOwnerAndName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;

    protected static string LastSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }
}
