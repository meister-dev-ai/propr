// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class GitReviewRepositoryWorkspace(
    ReviewRepositoryWorkspaceLease lease,
    GitCommandRunner gitCommandRunner,
    ILogger logger,
    ReviewWorkspaceCleanupService cleanupService) : IReviewRepositoryWorkspace
{
    public ReviewRepositoryWorkspaceLease Lease { get; } = lease;

    public async Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            this.Lease.HeadWorkspacePath,
            ["diff", "--name-status", $"{this.Lease.MergeBaseSha}...HEAD"],
            null,
            ct);
        result.EnsureSuccess("list changed files", "git diff --name-status <merge-base>...HEAD");

        var summaries = new List<ChangedFileSummary>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var changeType = MapChangeType(parts[0]);
            var path = changeType == ChangeType.Rename && parts.Length >= 3 ? parts[2] : parts[1];
            summaries.Add(new ChangedFileSummary(path.TrimStart('/'), changeType));
        }

        return summaries.AsReadOnly();
    }

    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
    {
        var root = this.ResolveRoot(branchSide);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public async Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
    {
        var root = this.ResolveRoot(branchSide);
        var normalizedPath = path.Trim().TrimStart('/').Replace('\\', '/');
        var candidatePath = Path.GetFullPath(Path.Combine(root, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidatePath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(candidatePath, Path.GetFullPath(root), StringComparison.Ordinal))
        {
            return null;
        }

        if (!File.Exists(candidatePath))
        {
            return null;
        }

        if (BinaryFileDetector.IsBinary(normalizedPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(candidatePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
    {
        var normalizedPath = path.Trim().TrimStart('/').Replace('\\', '/');
        var result = await gitCommandRunner.RunAsync(
            this.Lease.HeadWorkspacePath,
            ["diff", "--no-ext-diff", "--unified=3", this.Lease.MergeBaseSha, this.Lease.HeadSha, "--", normalizedPath],
            null,
            ct);
        result.EnsureSuccess("load unified diff", "git diff --unified=3 <merge-base> <head> -- <path>");
        return result.StandardOutput;
    }

    public async ValueTask DisposeAsync()
    {
        cleanupService.ReleaseLease(this.Lease);
        await this.RemoveWorktreeAsync(this.Lease.HeadWorkspacePath);
        await this.RemoveWorktreeAsync(this.Lease.BaseWorkspacePath);

        var workspaceRoot = Path.GetDirectoryName(this.Lease.HeadWorkspacePath);
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
        {
            try
            {
                Directory.Delete(workspaceRoot, true);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to delete released workspace root {WorkspaceRoot}.", workspaceRoot);
            }
        }
    }

    private async Task RemoveWorktreeAsync(string worktreePath)
    {
        var mirrorPath = this.Lease.MirrorPath;
        if (!Directory.Exists(worktreePath))
        {
            return;
        }

        try
        {
            var result = await gitCommandRunner.RunAsync(
                mirrorPath,
                ["worktree", "remove", "--force", worktreePath],
                null,
                CancellationToken.None);
            result.EnsureSuccess("remove worktree", "git worktree remove --force <path>");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove git worktree {WorktreePath}; deleting directory directly.", worktreePath);
            Directory.Delete(worktreePath, true);
        }
    }

    private string ResolveRoot(string branchSide)
    {
        return string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.OrdinalIgnoreCase)
            ? this.Lease.BaseWorkspacePath
            : this.Lease.HeadWorkspacePath;
    }

    private static ChangeType MapChangeType(string status)
    {
        var normalized = status.Trim().ToUpperInvariant();
        if (normalized == "A")
        {
            return ChangeType.Add;
        }

        if (normalized == "D")
        {
            return ChangeType.Delete;
        }

        if (normalized.StartsWith("R", StringComparison.Ordinal))
        {
            return ChangeType.Rename;
        }

        return ChangeType.Edit;
    }
}
