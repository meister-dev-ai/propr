// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.ProCursor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Shared Azure DevOps Git-backed materialization flow for ProCursor repository and wiki sources.
/// </summary>
public abstract class AdoGitProCursorMaterializerBase(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    IOptions<ProCursorOptions> options,
    ILogger logger) : IProCursorMaterializer
{
    private const string WorkspaceRootDirectoryName = "meisterpropr-procursor";
    private readonly TimeSpan _workspaceRetention = TimeSpan.FromMinutes(Math.Max(1, options.Value.TempWorkspaceRetentionMinutes));

    /// <inheritdoc />
    public abstract ProCursorSourceKind SourceKind { get; }

    /// <inheritdoc />
    public async Task<ProCursorMaterializedSource> MaterializeAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        string? requestedCommitSha,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trackedBranch);

        if (source.SourceKind != this.SourceKind)
        {
            throw new InvalidOperationException(
                $"Materializer {this.GetType().Name} cannot handle source kind {source.SourceKind}.");
        }

        if (trackedBranch.KnowledgeSourceId != source.Id)
        {
            throw new InvalidOperationException(
                $"Tracked branch {trackedBranch.Id} does not belong to source {source.Id}.");
        }

        var commitSha = await this.ResolveCommitShaAsync(source, trackedBranch, requestedCommitSha, ct);
        var discoveredPaths = await this.ListPathsAsync(source, commitSha, ct);
        var eligiblePaths = FilterPaths(discoveredPaths, source.RootPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CleanupStaleWorkspaces(source.Id, trackedBranch.Id, this._workspaceRetention);
        var rootDirectory = CreateRootDirectory(source.Id, trackedBranch.Id);
        Directory.CreateDirectory(rootDirectory);

        var materializedPaths = new List<string>(eligiblePaths.Count);

        try
        {
            foreach (var path in eligiblePaths)
            {
                ct.ThrowIfCancellationRequested();

                var content = await this.GetFileContentAsync(source, commitSha, path, ct);
                if (content is null)
                {
                    continue;
                }

                var outputPath = Path.Combine(rootDirectory, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8, ct);
                materializedPaths.Add(path);
            }

            this.LogMaterializedSource(
                logger,
                source.Id,
                trackedBranch.BranchName,
                commitSha,
                materializedPaths.Count);

            return new ProCursorMaterializedSource(
                source.Id,
                trackedBranch.Id,
                trackedBranch.BranchName,
                commitSha,
                rootDirectory,
                materializedPaths.AsReadOnly());
        }
        catch
        {
            DeleteDirectory(rootDirectory);
            throw;
        }
    }

    protected internal virtual async Task<string> ResolveCommitShaAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        string? requestedCommitSha,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedCommitSha))
        {
            return requestedCommitSha.Trim();
        }

        var gitClient = await this.GetGitClientAsync(source, ct);
        var repositoryId = await this.ResolveRepositoryIdAsync(source, ct);
        var branchName = NormalizeBranchName(trackedBranch.BranchName);

        GitBranchStats? branchRef;
        try
        {
            branchRef = await gitClient.GetBranchAsync(
                source.ProjectId,
            repositoryId,
                branchName,
                cancellationToken: ct);
        }
        catch (VssServiceResponseException ex)
        {
            throw new InvalidOperationException(
                $"Unable to resolve branch '{trackedBranch.BranchName}' for ProCursor source {source.Id}.",
                ex);
        }

        return branchRef?.Commit?.CommitId
               ?? throw new InvalidOperationException(
                   $"Branch '{trackedBranch.BranchName}' for ProCursor source {source.Id} does not currently resolve to a commit.");
    }

    protected internal virtual async Task<IReadOnlyList<string>> ListPathsAsync(
        ProCursorKnowledgeSource source,
        string commitSha,
        CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(source, ct);
        var repositoryId = await this.ResolveRepositoryIdAsync(source, ct);

        // Use GetItemsAsync with a commit-based version descriptor. This lets the server
        // resolve the commit and return all items without requiring a tree id.
        var versionDescriptor = new GitVersionDescriptor
        {
            Version = commitSha,
            VersionType = GitVersionType.Commit,
        };

        List<GitItem>? items;
        try
        {
            items = await gitClient.GetItemsAsync(
                source.ProjectId,
                repositoryId,
                null,
                VersionControlRecursionType.Full,
                versionDescriptor: versionDescriptor,
                userState: null,
                cancellationToken: ct);
        }
        catch
        {
            // If the commit can't be resolved or access is denied, treat as no paths.
            return new List<string>().AsReadOnly();
        }

        return (items ?? [])
            .Where(item => !item.IsFolder)
            .Select(item => NormalizeRepositoryPath(item.Path ?? string.Empty))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList()
            .AsReadOnly();
    }

    protected internal virtual async Task<string?> GetFileContentAsync(
        ProCursorKnowledgeSource source,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(source, ct);
        var repositoryId = await this.ResolveRepositoryIdAsync(source, ct);

        try
        {
            var item = await gitClient.GetItemAsync(
                source.ProjectId,
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

    protected internal virtual Task<string> ResolveRepositoryIdAsync(
        ProCursorKnowledgeSource source,
        CancellationToken ct)
    {
        return Task.FromResult(source.RepositoryId);
    }

    protected static string NormalizeBranchName(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;

    protected abstract void LogMaterializedSource(ILogger logger, Guid sourceId, string branchName, string commitSha, int fileCount);

    protected static string NormalizeRepositoryPath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }

    private async Task<GitHttpClient> GetGitClientAsync(ProCursorKnowledgeSource source, CancellationToken ct)
    {
        ClientAdoCredentials? credentials = null;
        if (source.ClientId != Guid.Empty)
        {
            credentials = await credentialRepository.GetByClientIdAsync(source.ClientId, ct);
        }

        var connection = await connectionFactory.GetConnectionAsync(source.OrganizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    private static IEnumerable<string> FilterPaths(IEnumerable<string> discoveredPaths, string? rootPath)
    {
        var normalizedRootPath = NormalizeRootPath(rootPath);

        foreach (var discoveredPath in discoveredPaths.Select(NormalizeRepositoryPath))
        {
            if (string.IsNullOrWhiteSpace(discoveredPath) || BinaryFileDetector.IsBinary(discoveredPath))
            {
                continue;
            }

            if (normalizedRootPath is null ||
                string.Equals(discoveredPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase) ||
                discoveredPath.StartsWith($"{normalizedRootPath}/", StringComparison.OrdinalIgnoreCase))
            {
                yield return discoveredPath;
            }
        }
    }

    private static string CreateRootDirectory(Guid sourceId, Guid trackedBranchId)
    {
        return Path.Combine(GetTrackedBranchWorkspaceRoot(sourceId, trackedBranchId), Guid.NewGuid().ToString("N"));
    }

    private static string GetTrackedBranchWorkspaceRoot(Guid sourceId, Guid trackedBranchId)
    {
        return Path.Combine(
            Path.GetTempPath(),
            WorkspaceRootDirectoryName,
            sourceId.ToString("N"),
            trackedBranchId.ToString("N"));
    }

    private static string? NormalizeRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.Equals(rootPath.Trim(), "/", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeRepositoryPath(rootPath);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static void CleanupStaleWorkspaces(Guid sourceId, Guid trackedBranchId, TimeSpan workspaceRetention)
    {
        var trackedBranchWorkspaceRoot = GetTrackedBranchWorkspaceRoot(sourceId, trackedBranchId);
        if (!Directory.Exists(trackedBranchWorkspaceRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - workspaceRetention;
        foreach (var workspaceDirectory in Directory.GetDirectories(trackedBranchWorkspaceRoot))
        {
            try
            {
                var workspaceInfo = new DirectoryInfo(workspaceDirectory);
                if (workspaceInfo.LastWriteTimeUtc < cutoff)
                {
                    workspaceInfo.Delete(true);
                }
            }
            catch (IOException)
            {
                // A concurrent worker may still own the workspace; leave it for a later cleanup pass.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort only.
            }
        }
    }
}
