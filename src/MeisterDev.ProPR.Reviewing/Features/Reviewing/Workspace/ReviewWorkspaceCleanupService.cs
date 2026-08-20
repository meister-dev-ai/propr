// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class ReviewWorkspaceCleanupService(
    IOptions<ReviewWorkspaceOptions> options,
    ILogger<ReviewWorkspaceCleanupService> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _mirrorReferenceCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _workspaceReferenceCounts = new(StringComparer.Ordinal);
    private readonly Lock _sizeCacheLock = new();
    private readonly Dictionary<string, DirectoryFileBytes> _directoryFileBytes = new(StringComparer.Ordinal);
    private int _sweepInProgress;

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

    /// <summary>
    ///     True while any review still holds a lease on this mirror. Callers that need the mirror to
    ///     themselves — repacking it, for instance — have to see this false before they start.
    /// </summary>
    public bool IsMirrorReferenced(string mirrorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);

        lock (this._lock)
        {
            return IsReferenced(this._mirrorReferenceCounts, mirrorPath);
        }
    }

    public Task RunCleanupAsync(CancellationToken ct)
    {
        logger.LogDebug("Review workspace cleanup requested for root {RootPath}.", options.Value.RootPath);

        Directory.CreateDirectory(this.RootPath);

        // Cleanup runs on every workspace preparation and is not required to run on any particular one, so a
        // caller that arrives while a sweep is in flight returns without starting a second. That also keeps
        // two sweeps from walking and deleting the same directories at the same time.
        if (Interlocked.CompareExchange(ref this._sweepInProgress, 1, 0) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            this.LogReferenceCounts();
            this.CleanupReleasedWorkspaces();
            this.CleanupMirrorCache();
        }
        finally
        {
            Interlocked.Exchange(ref this._sweepInProgress, 0);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     How many mirrors and workspaces are currently leased. On an instance whose review queue has
    ///     drained both return to zero; a count that stays above zero on an idle instance is a leaked lease,
    ///     which pins its mirror and checkout against cleanup until the process restarts.
    /// </summary>
    private void LogReferenceCounts()
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        int mirrorCount;
        int workspaceCount;
        lock (this._lock)
        {
            mirrorCount = this._mirrorReferenceCounts.Count;
            workspaceCount = this._workspaceReferenceCounts.Count;
        }

        logger.LogDebug(
            "Review workspace leases held: {MirrorCount} mirror(s), {WorkspaceCount} workspace(s).",
            mirrorCount,
            workspaceCount);
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
        var measured = new HashSet<string>(StringComparer.Ordinal);
        var mirrors = Directory.EnumerateDirectories(mirrorsRoot)
            .Select(path => new DirectoryInfo(path))
            .Select(directory => new MirrorEntry(directory, this.GetDirectorySize(directory.FullName, measured), directory.LastWriteTimeUtc))
            .OrderBy(entry => entry.LastWriteTimeUtc)
            .ToList();
        this.ForgetUnmeasuredDirectories(measured);

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

    /// <summary>
    ///     A directory's total size, with the per-file <c>stat</c> calls remembered per directory and repeated
    ///     only for directories whose own timestamp moved since the last sweep.
    /// </summary>
    /// <remarks>
    ///     Measuring every file in every mirror on every workspace preparation made preparation cost grow
    ///     with the size of the cache: a mirror accumulates a packfile per fetch, and the walk was paid even
    ///     when the cache was far below its budget. Directory timestamps move when entries are added or
    ///     removed, so a fetch re-measures the pack directory it wrote to and nothing else. A file whose
    ///     content is rewritten in place without its directory changing keeps its previous size here, which
    ///     is acceptable for a cache-budget decision: git's object files are written once and never edited,
    ///     and the files that are rewritten (refs, FETCH_HEAD) are negligible next to a budget in megabytes.
    /// </remarks>
    private long GetDirectorySize(string path, HashSet<string> measured)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return 0;
        }

        // The same spelling the cache is keyed by, so a directory measured now is never pruned as unseen.
        measured.Add(directory.FullName);

        long totalBytes = 0;
        try
        {
            foreach (var subdirectory in directory.EnumerateDirectories())
            {
                totalBytes += this.GetDirectorySize(subdirectory.FullName, measured);
            }

            totalBytes += this.GetFileBytes(directory);
        }
        catch (DirectoryNotFoundException)
        {
            // Another sweep or a dispose removed the directory while it was being measured. Whatever is
            // gone is no longer occupying the cache budget.
        }

        return totalBytes;
    }

    private long GetFileBytes(DirectoryInfo directory)
    {
        var lastWriteTimeUtc = directory.LastWriteTimeUtc;
        lock (this._sizeCacheLock)
        {
            if (this._directoryFileBytes.TryGetValue(directory.FullName, out var cached)
                && cached.LastWriteTimeUtc == lastWriteTimeUtc)
            {
                return cached.Bytes;
            }
        }

        var bytes = directory.EnumerateFiles().Sum(file => file.Length);
        lock (this._sizeCacheLock)
        {
            this._directoryFileBytes[directory.FullName] = new DirectoryFileBytes(lastWriteTimeUtc, bytes);
        }

        return bytes;
    }

    /// <summary>Drops cache entries for directories that no longer exist, so a deleted mirror is not remembered.</summary>
    private void ForgetUnmeasuredDirectories(HashSet<string> measured)
    {
        lock (this._sizeCacheLock)
        {
            foreach (var path in this._directoryFileBytes.Keys.Where(path => !measured.Contains(path)).ToList())
            {
                this._directoryFileBytes.Remove(path);
            }
        }
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

    private sealed record DirectoryFileBytes(DateTime LastWriteTimeUtc, long Bytes);
}
