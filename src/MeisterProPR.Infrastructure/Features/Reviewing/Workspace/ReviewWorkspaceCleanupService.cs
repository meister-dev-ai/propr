// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class ReviewWorkspaceCleanupService(
    IOptions<ReviewWorkspaceOptions> options,
    ILogger<ReviewWorkspaceCleanupService> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _mirrorReferenceCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _workspaceReferenceCounts = new(StringComparer.Ordinal);

    public string RootPath => options.Value.RootPath;

    public void RegisterLease(ReviewRepositoryWorkspaceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (this._lock)
        {
            Increment(this._mirrorReferenceCounts, lease.MirrorPath);
            Increment(this._workspaceReferenceCounts, GetWorkspaceRoot(lease));
        }
    }

    public void ReleaseLease(ReviewRepositoryWorkspaceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (this._lock)
        {
            Decrement(this._mirrorReferenceCounts, lease.MirrorPath);
            Decrement(this._workspaceReferenceCounts, GetWorkspaceRoot(lease));
        }
    }

    public Task RunCleanupAsync(CancellationToken ct)
    {
        logger.LogDebug("Review workspace cleanup requested for root {RootPath}.", options.Value.RootPath);

        Directory.CreateDirectory(this.RootPath);
        this.CleanupReleasedWorkspaces();
        this.CleanupMirrorCache();
        return Task.CompletedTask;
    }

    private void CleanupReleasedWorkspaces()
    {
        var workspacesRoot = Path.Combine(this.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return;
        }

        Dictionary<string, int> snapshot;
        lock (this._lock)
        {
            snapshot = new Dictionary<string, int>(this._workspaceReferenceCounts, StringComparer.Ordinal);
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, options.Value.RetentionMinutes));
        foreach (var workspaceDirectory in Directory.EnumerateDirectories(workspacesRoot))
        {
            if (IsReferenced(snapshot, workspaceDirectory))
            {
                continue;
            }

            var lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(workspaceDirectory);
            if (lastWriteTimeUtc > cutoff)
            {
                continue;
            }

            this.TryDeleteDirectory(workspaceDirectory);
        }
    }

    private void CleanupMirrorCache()
    {
        var mirrorsRoot = Path.Combine(this.RootPath, "mirrors");
        if (!Directory.Exists(mirrorsRoot))
        {
            return;
        }

        Dictionary<string, int> snapshot;
        lock (this._lock)
        {
            snapshot = new Dictionary<string, int>(this._mirrorReferenceCounts, StringComparer.Ordinal);
        }

        var budgetBytes = Math.Max(128, options.Value.MaxCacheSizeMegabytes) * 1024L * 1024L;
        var mirrors = Directory.EnumerateDirectories(mirrorsRoot)
            .Select(path => new DirectoryInfo(path))
            .Select(directory => new MirrorEntry(directory, GetDirectorySize(directory.FullName), directory.LastWriteTimeUtc))
            .OrderBy(entry => entry.LastWriteTimeUtc)
            .ToList();
        var totalBytes = mirrors.Sum(entry => entry.SizeBytes);
        if (totalBytes <= budgetBytes)
        {
            return;
        }

        foreach (var mirror in mirrors)
        {
            if (IsReferenced(snapshot, mirror.Directory.FullName))
            {
                continue;
            }

            this.TryDeleteDirectory(mirror.Directory.FullName);
            totalBytes -= mirror.SizeBytes;
            if (totalBytes <= budgetBytes)
            {
                return;
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete review workspace directory {Path} during cleanup.", path);
        }
    }

    private static long GetDirectorySize(string path)
    {
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static string GetWorkspaceRoot(ReviewRepositoryWorkspaceLease lease)
    {
        return Path.GetDirectoryName(lease.HeadWorkspacePath) ?? lease.HeadWorkspacePath;
    }

    private static void Increment(IDictionary<string, int> lookup, string key)
    {
        lookup[key] = lookup.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static void Decrement(IDictionary<string, int> lookup, string key)
    {
        if (!lookup.TryGetValue(key, out var current))
        {
            return;
        }

        if (current <= 1)
        {
            lookup.Remove(key);
            return;
        }

        lookup[key] = current - 1;
    }

    private static bool IsReferenced(IReadOnlyDictionary<string, int> lookup, string key)
    {
        return lookup.TryGetValue(key, out var count) && count > 0;
    }

    private sealed record MirrorEntry(DirectoryInfo Directory, long SizeBytes, DateTime LastWriteTimeUtc);
}
