// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.ProCursor;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

/// <summary>
///     Shared Azure DevOps Git-backed materialization flow for ProCursor repository and wiki sources.
/// </summary>
public abstract class AdoGitProCursorMaterializerBase(
    IProCursorScmBroker scmBroker,
    IOptions<ProCursorOptions> options,
    ILogger logger) : IProCursorMaterializer
{
    private const string WorkspaceRootDirectoryName = "meisterpropr-procursor";

    private readonly TimeSpan _workspaceRetention =
        TimeSpan.FromMinutes(Math.Max(1, options.Value.TempWorkspaceRetentionMinutes));

    private string? _activeCommitSha;
    private ProCursorScmMaterializationResponse? _activeMaterialization;

    private Guid? _activeSourceId;
    private Guid? _activeTrackedBranchId;

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
            throw new InvalidOperationException($"Materializer {this.GetType().Name} cannot handle source kind {source.SourceKind}.");
        }

        if (trackedBranch.KnowledgeSourceId != source.Id)
        {
            throw new InvalidOperationException($"Tracked branch {trackedBranch.Id} does not belong to source {source.Id}.");
        }

        var commitSha = await this.ResolveCommitShaAsync(source, trackedBranch, requestedCommitSha, ct);
        this.SetActiveMaterializationContext(source.Id, trackedBranch.Id, commitSha);

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

                var outputPath = Path.Combine(
                    rootDirectory,
                    path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
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
        finally
        {
            this.ClearActiveMaterializationContext();
        }
    }

    /// <summary>
    ///     Resolves the commit SHA for the given tracked branch.
    /// </summary>
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

        var commitSha = await scmBroker.GetLatestCommitShaAsync(ToDto(source), ToDto(trackedBranch), ct);
        return commitSha
               ?? throw new InvalidOperationException(
                   $"Branch '{trackedBranch.BranchName}' for ProCursor source {source.Id} does not currently resolve to a commit.");
    }

    /// <summary>
    ///     Lists all file paths in the repository at the specified commit.
    /// </summary>
    protected internal virtual async Task<IReadOnlyList<string>> ListPathsAsync(
        ProCursorKnowledgeSource source,
        string commitSha,
        CancellationToken ct)
    {
        var materialized = await this.GetOrLoadMaterializationAsync(source, commitSha, ct);
        return materialized.Files
            .Select(file => NormalizeRepositoryPath(file.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Gets the content of a file from the repository at the specified commit.
    /// </summary>
    protected internal virtual async Task<string?> GetFileContentAsync(
        ProCursorKnowledgeSource source,
        string commitSha,
        string path,
        CancellationToken ct)
    {
        var materialized = await this.GetOrLoadMaterializationAsync(source, commitSha, ct);
        return materialized.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRepositoryPath(file.Path), NormalizeRepositoryPath(path), StringComparison.OrdinalIgnoreCase))?.Content;
    }

    /// <summary>
    ///     Resolves the repository ID from the given ProCursor knowledge source.
    /// </summary>
    protected internal virtual Task<string> ResolveRepositoryIdAsync(
        ProCursorKnowledgeSource source,
        CancellationToken ct)
    {
        return Task.FromResult(source.RepositoryId);
    }

    /// <summary>
    ///     Logs the materialized source information.
    /// </summary>
    protected abstract void LogMaterializedSource(
        ILogger logger,
        Guid sourceId,
        string branchName,
        string commitSha,
        int fileCount);

    /// <summary>
    ///     Normalizes a repository path by converting backslashes to forward slashes and ensuring it starts with a forward slash.
    /// </summary>
    protected static string NormalizeRepositoryPath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.StartsWith('/') ? normalized : $"/{normalized}";
    }

    private async Task<ProCursorScmMaterializationResponse> GetOrLoadMaterializationAsync(
        ProCursorKnowledgeSource source,
        string commitSha,
        CancellationToken ct)
    {
        if (this._activeMaterialization is not null
            && this._activeSourceId == source.Id
            && string.Equals(this._activeCommitSha, commitSha, StringComparison.OrdinalIgnoreCase))
        {
            return this._activeMaterialization;
        }

        var trackedBranch = this.ResolveActiveTrackedBranch(source);
        this._activeMaterialization = await scmBroker.MaterializeAsync(ToDto(source), ToDto(trackedBranch), commitSha, ct);
        this._activeSourceId = source.Id;
        this._activeTrackedBranchId = trackedBranch.Id;
        this._activeCommitSha = commitSha;
        return this._activeMaterialization;
    }

    private ProCursorTrackedBranch ResolveActiveTrackedBranch(ProCursorKnowledgeSource source)
    {
        if (this._activeTrackedBranchId.HasValue)
        {
            var activeBranch = source.TrackedBranches.FirstOrDefault(branch => branch.Id == this._activeTrackedBranchId.Value);
            if (activeBranch is not null)
            {
                return activeBranch;
            }
        }

        return source.TrackedBranches.FirstOrDefault(branch =>
                   string.Equals(branch.BranchName, source.DefaultBranch, StringComparison.OrdinalIgnoreCase))
               ?? source.TrackedBranches.First();
    }

    private static ProCursorKnowledgeSourceDto ToDto(ProCursorKnowledgeSource source)
    {
        return new ProCursorKnowledgeSourceDto(
            source.Id,
            source.ClientId,
            source.DisplayName,
            source.SourceKind,
            source.ProviderScopePath,
            source.ProviderProjectKey,
            source.RepositoryId,
            source.DefaultBranch,
            source.RootPath,
            source.IsEnabled,
            source.SymbolMode,
            null,
            source.TrackedBranches.Select(ToDto).ToList().AsReadOnly(),
            source.OrganizationScopeId,
            string.IsNullOrWhiteSpace(source.CanonicalSourceProvider) || string.IsNullOrWhiteSpace(source.CanonicalSourceValue)
                ? null
                : new CanonicalSourceReferenceDto(source.CanonicalSourceProvider, source.CanonicalSourceValue),
            source.SourceDisplayName);
    }

    private static ProCursorTrackedBranchDto ToDto(ProCursorTrackedBranch trackedBranch)
    {
        return new ProCursorTrackedBranchDto(
            trackedBranch.Id,
            trackedBranch.BranchName,
            trackedBranch.RefreshTriggerMode,
            trackedBranch.MiniIndexEnabled,
            trackedBranch.LastSeenCommitSha,
            trackedBranch.LastIndexedCommitSha,
            trackedBranch.IsEnabled,
            "unknown");
    }

    private void SetActiveMaterializationContext(Guid sourceId, Guid trackedBranchId, string commitSha)
    {
        this._activeSourceId = sourceId;
        this._activeTrackedBranchId = trackedBranchId;
        this._activeCommitSha = commitSha;
        this._activeMaterialization = null;
    }

    private void ClearActiveMaterializationContext()
    {
        this._activeSourceId = null;
        this._activeTrackedBranchId = null;
        this._activeCommitSha = null;
        this._activeMaterialization = null;
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

            if (normalizedRootPath is null
                || string.Equals(discoveredPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || discoveredPath.StartsWith($"{normalizedRootPath}/", StringComparison.OrdinalIgnoreCase))
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
