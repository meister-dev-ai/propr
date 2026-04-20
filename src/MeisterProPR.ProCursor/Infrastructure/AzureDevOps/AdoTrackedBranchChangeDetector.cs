// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Wiki.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Azure DevOps-backed tracked-branch head detector for ProCursor scheduling.
/// </summary>
public sealed partial class AdoTrackedBranchChangeDetector(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoTrackedBranchChangeDetector> logger) : IProCursorTrackedBranchChangeDetector
{
    /// <summary>
    /// Gets the latest commit SHA for a tracked branch.
    /// </summary>
    /// <param name="source">The ProCursor knowledge source.</param>
    /// <param name="trackedBranch">The tracked branch to get the commit SHA for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The commit SHA if found; otherwise null.</returns>
    public async Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default)
    {
        var credentials = source.ClientId != Guid.Empty
            ? await this.ResolveConnectionCredentialsAsync(source.ClientId, source.ProviderScopePath, ct)
            : null;

        try
        {
            var repositoryId = await this.ResolveRepositoryIdAsync(source, credentials, ct);
            var branch = await this.GetBranchAsync(source, repositoryId, trackedBranch.BranchName, credentials, ct);

            return branch?.Commit?.CommitId;
        }
        catch (VssServiceResponseException ex)
        {
            LogBranchHeadResolutionFailed(logger, source.Id, trackedBranch.Id, trackedBranch.BranchName, ex);
            return null;
        }
    }

    private static string NormalizeBranchName(string branchName)
    {
        return branchName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branchName["refs/heads/".Length..]
            : branchName;
    }

    private async Task<string> ResolveRepositoryIdAsync(
        ProCursorKnowledgeSource source,
        AdoServicePrincipalCredentials? credentials,
        CancellationToken ct)
    {
        if (source.SourceKind != ProCursorSourceKind.AdoWiki)
        {
            return source.RepositoryId;
        }

        var wikis = await this.ListWikisAsync(source, credentials, ct);
        return AdoWikiRepositoryResolver.ResolveRepositoryId(source, wikis);
    }

    private async Task<IReadOnlyList<WikiV2>> ListWikisAsync(
        ProCursorKnowledgeSource source,
        AdoServicePrincipalCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.ProviderScopePath, credentials, ct);
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(source.ProviderProjectKey, null, ct);
        return wikis.ToList().AsReadOnly();
    }

    private async Task<GitBranchStats?> GetBranchAsync(
        ProCursorKnowledgeSource source,
        string repositoryId,
        string branchName,
        AdoServicePrincipalCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.ProviderScopePath, credentials, ct);
        var gitClient = connection.GetClient<GitHttpClient>();
        return await gitClient.GetBranchAsync(
            source.ProviderProjectKey,
            repositoryId,
            NormalizeBranchName(branchName),
            cancellationToken: ct);
    }

    private async Task<AdoServicePrincipalCredentials?> ResolveConnectionCredentialsAsync(
        Guid clientId,
        string organizationUrl,
        CancellationToken ct)
    {
        var connection = await connectionRepository.GetOperationalConnectionAsync(
            clientId,
            new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl),
            ct);

        if (connection is null ||
            string.IsNullOrWhiteSpace(connection.OAuthTenantId) ||
            string.IsNullOrWhiteSpace(connection.OAuthClientId) ||
            string.IsNullOrWhiteSpace(connection.Secret))
        {
            return null;
        }

        return new AdoServicePrincipalCredentials(
            connection.OAuthTenantId,
            connection.OAuthClientId,
            connection.Secret);
    }
}
