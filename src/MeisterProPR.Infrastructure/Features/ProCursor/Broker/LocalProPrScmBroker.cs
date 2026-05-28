// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Wiki.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Broker;

/// <summary>
///     ProPR-owned SCM broker backend used by internal ProPR broker endpoints.
/// </summary>
public sealed class LocalProPrScmBroker(
    IClientAdminService clientAdminService,
    IClientScmConnectionRepository connectionRepository,
    VssConnectionFactory connectionFactory) : IProCursorScmBroker
{
    public async Task<string?> GetLatestCommitShaAsync(
        ProCursorKnowledgeSourceDto source,
        ProCursorTrackedBranchDto trackedBranch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackedBranch);

        await this.EnsureClientExistsAsync(source.ClientId, ct);

        var credentials = await this.ResolveConnectionCredentialsAsync(source.ClientId, source.ProviderScopePath, ct);

        try
        {
            var repositoryId = source.SourceKind == ProCursorSourceKind.AdoWiki
                ? await this.ResolveWikiRepositoryIdAsync(source, credentials, ct)
                : source.RepositoryId;
            var gitClient = await this.GetGitClientAsync(source.ProviderScopePath, credentials, ct);
            var branch = await gitClient.GetBranchAsync(
                source.ProviderProjectKey,
                repositoryId,
                NormalizeBranchName(trackedBranch.BranchName),
                cancellationToken: ct);

            return branch?.Commit?.CommitId;
        }
        catch (VssServiceResponseException)
        {
            return null;
        }
    }

    public async Task<ProCursorScmMaterializationResponse> MaterializeAsync(
        ProCursorKnowledgeSourceDto source,
        ProCursorTrackedBranchDto trackedBranch,
        string? requestedCommitSha,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackedBranch);

        await this.EnsureClientExistsAsync(source.ClientId, ct);

        var credentials = await this.ResolveConnectionCredentialsAsync(source.ClientId, source.ProviderScopePath, ct);
        var repositoryId = source.SourceKind == ProCursorSourceKind.AdoWiki
            ? await this.ResolveWikiRepositoryIdAsync(source, credentials, ct)
            : source.RepositoryId;
        var gitClient = await this.GetGitClientAsync(source.ProviderScopePath, credentials, ct);

        var commitSha = !string.IsNullOrWhiteSpace(requestedCommitSha)
            ? requestedCommitSha.Trim()
            : await ResolveHeadCommitShaAsync(gitClient, source, repositoryId, trackedBranch, ct);

        var versionDescriptor = new GitVersionDescriptor
        {
            Version = commitSha,
            VersionType = GitVersionType.Commit,
        };

        List<GitItem>? items;
        try
        {
            items = await gitClient.GetItemsAsync(
                source.ProviderProjectKey,
                repositoryId,
                null,
                VersionControlRecursionType.Full,
                versionDescriptor: versionDescriptor,
                userState: null,
                cancellationToken: ct);
        }
        catch (VssServiceResponseException ex)
        {
            throw new InvalidOperationException(
                $"Unable to enumerate source files for ProCursor source {source.Id} at commit '{commitSha}'.",
                ex);
        }

        var normalizedRootPath = NormalizeRootPath(source.RootPath);
        var candidatePaths = (items ?? [])
            .Where(item => !item.IsFolder)
            .Select(item => NormalizeRepositoryPath(item.Path ?? string.Empty))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !BinaryFileDetector.IsBinary(path))
            .Where(path => normalizedRootPath is null
                           || string.Equals(path, normalizedRootPath, StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith($"{normalizedRootPath}/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new List<ProCursorScmFileDto>(candidatePaths.Count);
        foreach (var path in candidatePaths)
        {
            var item = await TryGetFileAsync(gitClient, source, repositoryId, commitSha, path, ct);
            if (!string.IsNullOrEmpty(item))
            {
                files.Add(new ProCursorScmFileDto(path, item));
            }
        }

        return new ProCursorScmMaterializationResponse(commitSha, files.AsReadOnly());
    }

    private async Task EnsureClientExistsAsync(Guid clientId, CancellationToken ct)
    {
        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            throw new KeyNotFoundException($"Client {clientId} was not found.");
        }
    }

    private async Task<GitHttpClient> GetGitClientAsync(
        string organizationUrl,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    private async Task<IReadOnlyList<WikiV2>> ListWikisAsync(
        ProCursorKnowledgeSourceDto source,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        var connection = await connectionFactory.GetConnectionAsync(source.ProviderScopePath, credentials, ct);
        var wikiClient = connection.GetClient<WikiHttpClient>();
        var wikis = await wikiClient.GetAllWikisAsync(source.ProviderProjectKey, null, ct);
        return wikis.ToList().AsReadOnly();
    }

    private async Task<string> ResolveWikiRepositoryIdAsync(
        ProCursorKnowledgeSourceDto source,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        var canonicalWikiId = !string.IsNullOrWhiteSpace(source.CanonicalSourceRef?.Value)
            ? source.CanonicalSourceRef.Value.Trim()
            : source.RepositoryId;
        var wikis = await this.ListWikisAsync(source, credentials, ct);
        var wiki = wikis.FirstOrDefault(candidate =>
                       string.Equals(candidate.Id.ToString(), canonicalWikiId, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(candidate.RepositoryId.ToString(), canonicalWikiId, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(candidate.Name, canonicalWikiId, StringComparison.OrdinalIgnoreCase)
                       || (!string.IsNullOrWhiteSpace(source.SourceDisplayName)
                           && string.Equals(candidate.Name, source.SourceDisplayName, StringComparison.OrdinalIgnoreCase)))
                   ?? throw new InvalidOperationException($"Unable to resolve the backing repository for wiki source {source.Id}.");

        return wiki.RepositoryId.ToString();
    }

    private static async Task<string?> TryGetFileAsync(
        GitHttpClient gitClient,
        ProCursorKnowledgeSourceDto source,
        string repositoryId,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        try
        {
            var item = await gitClient.GetItemAsync(
                source.ProviderProjectKey,
                repositoryId,
                path,
                null,
                null,
                null,
                null,
                null,
                new GitVersionDescriptor
                {
                    Version = commitSha,
                    VersionType = GitVersionType.Commit,
                },
                true,
                null,
                null,
                null,
                ct);

            return item?.Content;
        }
        catch (VssServiceResponseException)
        {
            return null;
        }
    }

    private async Task<AdoConnectionCredentials?> ResolveConnectionCredentialsAsync(
        Guid clientId,
        string organizationUrl,
        CancellationToken ct)
    {
        var connection = await connectionRepository.GetOperationalConnectionAsync(
            clientId,
            new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl),
            ct);

        if (connection is null)
        {
            return null;
        }

        return connection.AuthenticationKind switch
        {
            ScmAuthenticationKind.OAuthClientCredentials
                when !string.IsNullOrWhiteSpace(connection.OAuthTenantId)
                     && !string.IsNullOrWhiteSpace(connection.OAuthClientId)
                => AdoConnectionCredentials.ForOAuthClientCredentials(
                    connection.OAuthTenantId,
                    connection.OAuthClientId,
                    connection.Secret),
            ScmAuthenticationKind.PersonalAccessToken
                => AdoConnectionCredentials.ForPersonalAccessToken(connection.Secret),
            ScmAuthenticationKind.WindowsUserAccount
                when !string.IsNullOrWhiteSpace(connection.UserName)
                => AdoConnectionCredentials.ForWindowsUserAccount(connection.UserName, connection.Secret),
            _ => null,
        };
    }

    private static async Task<string> ResolveHeadCommitShaAsync(
        GitHttpClient gitClient,
        ProCursorKnowledgeSourceDto source,
        string repositoryId,
        ProCursorTrackedBranchDto trackedBranch,
        CancellationToken ct)
    {
        try
        {
            var branch = await gitClient.GetBranchAsync(
                source.ProviderProjectKey,
                repositoryId,
                NormalizeBranchName(trackedBranch.BranchName),
                cancellationToken: ct);
            return branch?.Commit?.CommitId
                   ?? throw new InvalidOperationException(
                       $"Branch '{trackedBranch.BranchName}' for ProCursor source {source.Id} does not currently resolve to a commit.");
        }
        catch (VssServiceResponseException ex)
        {
            throw new InvalidOperationException(
                $"Unable to resolve branch '{trackedBranch.BranchName}' for ProCursor source {source.Id}.",
                ex);
        }
    }

    private static string NormalizeBranchName(string branchName)
    {
        return branchName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branchName["refs/heads/".Length..]
            : branchName;
    }

    private static string NormalizeRepositoryPath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }

    private static string? NormalizeRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.Equals(rootPath.Trim(), "/", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeRepositoryPath(rootPath);
    }
}
