// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Wiki.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;

/// <summary>
///     Azure DevOps-backed discovery service for guided admin configuration flows.
/// </summary>
public class AdoDiscoveryService(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository) : IProviderAdminDiscoveryService
{
    private const string AzureDevOpsProvider = "azureDevOps";

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task<ClientScmScopeDto?> GetScopeAsync(
        Guid clientId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        return AdoProviderAdapterHelpers.ResolveOrganizationScopeByIdAsync(
            connectionRepository,
            scopeRepository,
            clientId,
            scopeId,
            ct);
    }

    public async Task<IReadOnlyList<AdoProjectOptionDto>> ListProjectsAsync(
        Guid clientId,
        Guid organizationScopeId,
        CancellationToken ct = default)
    {
        var (_, _, connection) = await this.ResolveScopeAsync(clientId, organizationScopeId, ct);
        var projects = await this.GetProjectsAsync(connection, ct);

        return projects
            .Select(project => new AdoProjectOptionDto(
                organizationScopeId,
                project.Id.ToString(),
                string.IsNullOrWhiteSpace(project.Name) ? project.Id.ToString() : project.Name))
            .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyList<AdoSourceOptionDto>> ListSourcesAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var (_, _, connection) = await this.ResolveScopeAsync(clientId, organizationScopeId, ct);
        return sourceKind switch
        {
            ProCursorSourceKind.Repository => await this.ListRepositoriesAsync(connection, projectId, ct),
            ProCursorSourceKind.AdoWiki => await this.ListWikisAsync(connection, projectId, ct),
            _ => [],
        };
    }

    public async Task<IReadOnlyList<AdoBranchOptionDto>> ListBranchesAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        ProCursorSourceKind sourceKind,
        CanonicalSourceReferenceDto canonicalSourceRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(canonicalSourceRef);

        var (_, _, connection) = await this.ResolveScopeAsync(clientId, organizationScopeId, ct);
        var repositoryId = await this.ResolveRepositoryIdAsync(
            connection,
            projectId,
            sourceKind,
            canonicalSourceRef,
            ct);
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return [];
        }

        var repository = await this.GetRepositoryAsync(connection, projectId, repositoryId, ct);
        var defaultBranch = NormalizeBranchName(repository.DefaultBranch);
        var branches = await this.GetBranchesAsync(connection, projectId, repositoryId, ct);

        return branches
            .Select(branch => NormalizeBranchName(branch.Name))
            .Where(static branchName => !string.IsNullOrWhiteSpace(branchName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(branchName => branchName, StringComparer.OrdinalIgnoreCase)
            .Select(branchName => new AdoBranchOptionDto(
                branchName!,
                string.Equals(branchName, defaultBranch, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyList<AdoCrawlFilterOptionDto>> ListCrawlFiltersAsync(
        Guid clientId,
        Guid organizationScopeId,
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var (_, _, connection) = await this.ResolveScopeAsync(clientId, organizationScopeId, ct);
        var repositories = await this.GetRepositoriesAsync(connection, projectId, ct);

        return repositories
            .Select(repository => new AdoCrawlFilterOptionDto(
                new CanonicalSourceReferenceDto(AzureDevOpsProvider, repository.Id.ToString()),
                repository.Name ?? repository.Id.ToString(),
                BuildBranchSuggestions(repository.DefaultBranch)))
            .OrderBy(repository => repository.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    protected internal virtual async Task<IReadOnlyList<AdoSourceOptionDto>> ListRepositoriesAsync(
        VssConnection connection,
        string projectId,
        CancellationToken ct)
    {
        var repositories = await this.GetRepositoriesAsync(connection, projectId, ct);

        return repositories
            .Select(repository => new AdoSourceOptionDto(
                ProCursorSourceKind.Repository.ToString("G"),
                new CanonicalSourceReferenceDto(AzureDevOpsProvider, repository.Id.ToString()),
                repository.Name ?? repository.Id.ToString(),
                NormalizeBranchName(repository.DefaultBranch)))
            .OrderBy(repository => repository.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    protected internal virtual async Task<IReadOnlyList<AdoSourceOptionDto>> ListWikisAsync(
        VssConnection connection,
        string projectId,
        CancellationToken ct)
    {
        var wikis = await this.GetWikisAsync(connection, projectId, ct);

        return wikis
            .Select(wiki => new AdoSourceOptionDto(
                ProCursorSourceKind.AdoWiki.ToString("G"),
                new CanonicalSourceReferenceDto(AzureDevOpsProvider, wiki.Id.ToString()),
                wiki.Name ?? wiki.Id.ToString(),
                NormalizeBranchName(wiki.Versions?.FirstOrDefault()?.Version)))
            .OrderBy(wiki => wiki.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    protected internal virtual async Task<string?> ResolveRepositoryIdAsync(
        VssConnection connection,
        string projectId,
        ProCursorSourceKind sourceKind,
        CanonicalSourceReferenceDto canonicalSourceRef,
        CancellationToken ct)
    {
        if (!string.Equals(canonicalSourceRef.Provider, AzureDevOpsProvider, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return sourceKind switch
        {
            ProCursorSourceKind.Repository => canonicalSourceRef.Value,
            ProCursorSourceKind.AdoWiki => await this.ResolveWikiRepositoryIdAsync(
                connection,
                projectId,
                canonicalSourceRef.Value,
                ct),
            _ => null,
        };
    }

    private static IReadOnlyList<AdoBranchOptionDto> BuildBranchSuggestions(string? defaultBranch)
    {
        var normalizedDefaultBranch = NormalizeBranchName(defaultBranch);
        if (string.IsNullOrWhiteSpace(normalizedDefaultBranch))
        {
            return [];
        }

        return [new AdoBranchOptionDto(normalizedDefaultBranch, true)];
    }

    private static string? NormalizeBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return null;
        }

        return branchName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branchName["refs/heads/".Length..]
            : branchName;
    }

    protected internal virtual async Task<string?> ResolveWikiRepositoryIdAsync(
        VssConnection connection,
        string projectId,
        string wikiId,
        CancellationToken ct)
    {
        var wikis = await this.GetWikisAsync(connection, projectId, ct);
        var wiki = wikis.FirstOrDefault(candidate => string.Equals(
            candidate.Id.ToString(),
            wikiId,
            StringComparison.OrdinalIgnoreCase));
        if (wiki is null || wiki.RepositoryId == Guid.Empty)
        {
            return null;
        }

        return wiki.RepositoryId.ToString();
    }

    protected internal virtual async
        Task<(ClientAdoOrganizationScopeDto Scope, AdoServicePrincipalCredentials? Credentials, VssConnection Connection
            )> ResolveScopeAsync(
            Guid clientId,
            Guid organizationScopeId,
            CancellationToken ct)
    {
        var scope = await AdoProviderAdapterHelpers.ResolveOrganizationScopeByIdAsync(
                        connectionRepository,
                        scopeRepository,
                        clientId,
                        organizationScopeId,
                        ct)
                    ?? throw new KeyNotFoundException($"Organization scope {organizationScopeId} was not found for client {clientId}.");

        if (!scope.IsEnabled)
        {
            throw new InvalidOperationException("The selected organization scope is disabled.");
        }

        var organizationUrl = scope.ScopePath;
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return (AdoProviderAdapterHelpers.ToAdoOrganizationScopeDto(scope), credentials, connection);
    }

    protected internal virtual async Task<IReadOnlyList<TeamProjectReference>> GetProjectsAsync(
        VssConnection connection,
        CancellationToken ct)
    {
        var projectClient = connection.GetClient<ProjectHttpClient>();
        var projects = await projectClient.GetProjects(top: 500, skip: 0, continuationToken: null);
        return projects.ToList().AsReadOnly();
    }

    protected internal virtual async Task<IReadOnlyList<GitRepository>> GetRepositoriesAsync(
        VssConnection connection,
        string projectId,
        CancellationToken ct)
    {
        var gitClient = connection.GetClient<GitHttpClient>();
        var repositories = await gitClient.GetRepositoriesAsync(projectId, includeHidden: false, cancellationToken: ct);
        return repositories.ToList().AsReadOnly();
    }

    protected internal virtual Task<GitRepository> GetRepositoryAsync(
        VssConnection connection,
        string projectId,
        string repositoryId,
        CancellationToken ct)
    {
        var gitClient = connection.GetClient<GitHttpClient>();
        return gitClient.GetRepositoryAsync(projectId, repositoryId, cancellationToken: ct);
    }

    protected internal virtual async Task<IReadOnlyList<GitBranchStats>> GetBranchesAsync(
        VssConnection connection,
        string projectId,
        string repositoryId,
        CancellationToken ct)
    {
        var gitClient = connection.GetClient<GitHttpClient>();
        var branches = await gitClient.GetBranchesAsync(projectId, repositoryId, cancellationToken: ct);
        return branches.ToList().AsReadOnly();
    }

    protected internal virtual async Task<IReadOnlyList<WikiV2>> GetWikisAsync(
        VssConnection connection,
        string projectId,
        CancellationToken ct)
    {
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(projectId, null, ct);
        return wikis.ToList().AsReadOnly();
    }
}
