// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
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
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoTrackedBranchChangeDetector> logger) : IProCursorTrackedBranchChangeDetector
{
    public async Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        CancellationToken ct = default)
    {
        ClientAdoCredentials? credentials = null;
        if (source.ClientId != Guid.Empty)
        {
            credentials = await credentialRepository.GetByClientIdAsync(source.ClientId, ct);
        }

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
        ClientAdoCredentials? credentials,
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
        ClientAdoCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.OrganizationUrl, credentials, ct);
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(source.ProjectId, null, ct);
        return wikis.ToList().AsReadOnly();
    }

    private async Task<GitBranchStats?> GetBranchAsync(
        ProCursorKnowledgeSource source,
        string repositoryId,
        string branchName,
        ClientAdoCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.OrganizationUrl, credentials, ct);
        var gitClient = connection.GetClient<GitHttpClient>();
        return await gitClient.GetBranchAsync(
            source.ProjectId,
            repositoryId,
            NormalizeBranchName(branchName),
            cancellationToken: ct);
    }
}
