// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

/// <summary>
///     Reads one prepared review revision pair: head file contents from a checkout, everything else from
///     the mirror's object store.
/// </summary>
/// <remarks>
///     Only the head revision is checked out. The target side is read with <c>git cat-file</c> and
///     <c>git ls-tree</c> against the base commit instead, because that is all it was ever used for — the
///     diffs run against the head checkout and resolve their objects from the mirror. A second checkout is
///     a second complete materialisation of the repository, which was the largest single write a job made to
///     the workspace disk and doubled its exposure to running out of space mid-checkout.
///     <para>
///         Both file listings are read from the object store as well. Only the file contents of the head
///         revision come off the disk, where the checkout already holds them.
///     </para>
/// </remarks>
/// <param name="lease">The prepared workspace.</param>
/// <param name="gitCommandRunner">Runs the git commands behind the reads.</param>
/// <param name="logger">Logger for read failures that are reported as content-unavailable.</param>
/// <param name="cleanupService">Holds the reference counts this workspace's lease is registered in.</param>
/// <param name="authEnvironment">
///     Credentials for the mirror's remote, needed only when the mirror is a partial clone: an object that
///     was filtered out at fetch time is downloaded at the moment a read asks for it, and that download is
///     authenticated like any other fetch. Null where every object is present locally.
/// </param>
internal sealed class GitReviewRepositoryWorkspace(
    ReviewRepositoryWorkspaceLease lease,
    GitCommandRunner gitCommandRunner,
    ILogger logger,
    ReviewWorkspaceCleanupService cleanupService,
    IReadOnlyDictionary<string, string?>? authEnvironment = null) : IReviewRepositoryWorkspace
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> _fileTrees = new(StringComparer.Ordinal);
    private int _disposed;

    public ReviewRepositoryWorkspaceLease Lease { get; } = lease;

    public async Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        // -z separates every field with NUL and prints paths literally, so a filename carrying spaces
        // survives. Splitting lines on tabs and trimming them renamed " leading.cs " here while the tree
        // listing and the reads reported it as stored, and the review then read a path that does not exist.
        var result = await gitCommandRunner.RunAsync(
            this.Lease.HeadWorkspacePath,
            ["diff", "--name-status", "-z", $"{this.Lease.MergeBaseSha}...HEAD"],
            authEnvironment,
            ct,
            preserveStandardOutput: true);
        result.EnsureSuccess("list changed files", "git diff --name-status -z <merge-base>...HEAD");

        var fields = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var summaries = new List<ChangedFileSummary>();
        for (var index = 0; index < fields.Length;)
        {
            var changeType = MapChangeType(fields[index]);

            // A rename or a copy is reported as three fields, the status followed by the old path and the
            // new one; everything else is two. The new path is the one a review reads.
            var pathFields = changeType == ChangeType.Rename || fields[index].StartsWith('C') ? 2 : 1;
            if (index + pathFields >= fields.Length)
            {
                break;
            }

            summaries.Add(new ChangedFileSummary(fields[index + pathFields].TrimStart('/'), changeType));
            index += pathFields + 1;
        }

        return summaries.AsReadOnly();
    }

    /// <summary>
    ///     The file paths on one side of the review.
    /// </summary>
    /// <remarks>
    ///     Computed once per side and kept. Both revisions are fixed for the lifetime of the lease, so the
    ///     answer cannot change, and the agentic loop asks for it repeatedly while reviewing a file — every
    ///     one of those calls used to walk the whole checkout and sort it again.
    /// </remarks>
    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
    {
        var side = IsTargetSide(branchSide) ? RepositorySearchBranchSides.Target : RepositorySearchBranchSides.Source;

        // The load is wrapped in a Lazy because GetOrAdd may run its factory more than once under
        // concurrency, and only the Lazy's own value is ever observed, so the command runs once per side. It
        // runs without a caller's token, and each caller waits on it with its own: binding the shared load to
        // the token of whichever caller created it would hand that caller's cancellation to all the others.
        var shared = this._fileTrees.GetOrAdd(
            side,
            key => new Lazy<Task<IReadOnlyList<string>>>(
                () => this.LoadFileTreeAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return shared.Value.WaitAsync(ct);
    }

    public async Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (BinaryFileDetector.IsBinary(normalizedPath))
        {
            return null;
        }

        return IsTargetSide(branchSide)
            ? await this.ReadTargetFileAsync(normalizedPath, ct)
            : await this.ReadHeadFileAsync(normalizedPath, ct);
    }

    public async Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        var result = await gitCommandRunner.RunAsync(
            this.Lease.HeadWorkspacePath,
            ["diff", "--no-ext-diff", "--unified=3", this.Lease.MergeBaseSha, this.Lease.HeadSha, "--", normalizedPath],
            authEnvironment,
            ct);
        result.EnsureSuccess("load unified diff", "git diff --unified=3 <merge-base> <head> -- <path>");
        return result.StandardOutput;
    }

    /// <remarks>
    ///     Idempotent, and the reference counts depend on it: a lease released twice drops a mirror's count
    ///     to zero while another job is still reading it, and cleanup then deletes a mirror in use. Callers
    ///     may therefore dispose on every exit path without tracking whether someone else already did.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) == 1)
        {
            return;
        }

        try
        {
            // The removal runs with the mirror as its working directory, so the mirror has to stay
            // referenced until it is done. Releasing the lease first leaves a window in which another job's
            // cleanup sees the mirror as unreferenced and deletes it mid-dispose.
            await this.RemoveWorktreeAsync(this.Lease.HeadWorkspacePath);

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
        finally
        {
            cleanupService.ReleaseLease(this.Lease);
        }
    }

    private async Task<IReadOnlyList<string>> LoadFileTreeAsync(string side)
    {
        try
        {
            var revision = string.Equals(side, RepositorySearchBranchSides.Target, StringComparison.Ordinal)
                ? this.Lease.BaseSha
                : this.Lease.HeadSha;
            return await this.LoadRevisionFileTreeAsync(revision, CancellationToken.None);
        }
        catch
        {
            // Nothing is cached for a side that failed to load. The failure may be the cancellation of one
            // caller's token, in which case a later call with a live token would succeed.
            this._fileTrees.TryRemove(side, out _);
            throw;
        }
    }

    /// <summary>Lists the files one revision holds, out of the mirror's object store.</summary>
    /// <remarks>
    ///     Both sides are listed this way. Walking the head checkout instead reported what the filesystem
    ///     holds rather than what the revision holds: a directory the revision stores as a symbolic link was
    ///     followed, so files from outside the checkout were listed as repository paths, and a link to a
    ///     directory this process may not read ended the listing with an access error instead of a file list.
    ///     <para>
    ///         -z separates the records with NUL and prints paths literally: unquoted, and with any leading
    ///         or trailing whitespace in a filename intact. Splitting lines and trimming them would rename
    ///         " leading.txt" to "leading.txt", and a later read of that path would find nothing.
    ///     </para>
    ///     <para>
    ///         The mode and type are kept rather than asking for names alone, because a tree also carries
    ///         entries that are not files. A submodule appears as a commit, and listing it as a file offers a
    ///         path whose content this repository does not hold.
    ///     </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> LoadRevisionFileTreeAsync(string revision, CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            this.Lease.MirrorPath,
            ["ls-tree", "-r", "-z", revision],
            authEnvironment,
            ct,
            preserveStandardOutput: true);
        result.EnsureSuccess("list the files in a revision", "git ls-tree -r -z <revision>");

        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .Where(entry => entry.IsBlob)
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Reads a file as it is in the base revision, out of the mirror's object store.</summary>
    private async Task<string?> ReadTargetFileAsync(string normalizedPath, CancellationToken ct)
    {
        if (normalizedPath.Length == 0)
        {
            return null;
        }

        // The blob form of the command fails when the path names a tree. The general form would return the
        // tree's listing, which would then be returned as if it were file content.
        var result = await gitCommandRunner.RunAsync(
            this.Lease.MirrorPath,
            ["cat-file", "blob", $"{this.Lease.BaseSha}:{normalizedPath}"],
            authEnvironment,
            ct,
            preserveStandardOutput: true);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        // A failed read is not the same answer as an absent file, and returning null for both would report a
        // file the review cannot read as one the pull request adds. On a partial clone the read reaches the
        // server for the content, so it can fail for want of a network or a credential while the file is
        // there. The tree entry says which case this is, and reading it needs no content of its own.
        var entry = await gitCommandRunner.RunAsync(
            this.Lease.MirrorPath,
            ["ls-tree", "-z", this.Lease.BaseSha, "--", normalizedPath],
            authEnvironment,
            ct,
            preserveStandardOutput: true);
        entry.EnsureSuccess("classify a target-side path", "git ls-tree -z <base> -- <path>");

        var records = entry.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0)
        {
            logger.LogDebug(
                "No target-side content for {Path} at {BaseSha}: the path is not in that revision.",
                normalizedPath,
                this.Lease.BaseSha);
            return null;
        }

        if (!ParseTreeEntry(records[0]).IsBlob)
        {
            // A submodule is the case this covers: the entry names a commit in another repository, which this
            // one does not hold and a review cannot read as text.
            logger.LogDebug(
                "No target-side content for {Path} at {BaseSha}: the entry is not a file.",
                normalizedPath,
                this.Lease.BaseSha);
            return null;
        }

        result.EnsureSuccess(
            $"read the target-side content of a file present at {this.Lease.BaseSha}",
            "git cat-file blob <base>:<path>");
        return result.StandardOutput;
    }

    private async Task<string?> ReadHeadFileAsync(string normalizedPath, CancellationToken ct)
    {
        var root = Path.GetFullPath(this.Lease.HeadWorkspacePath);
        var candidatePath = Path.GetFullPath(Path.Combine(root, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidatePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        var link = InspectLink(candidatePath, root, logger);
        if (link.LeadsThroughALink)
        {
            return null;
        }

        if (link.Target is not null)
        {
            // What the revision holds at this path is the link, and git stores that as the target text, so
            // the text is the content and it is what a target-side read returns for the same path. Opening
            // the path instead read whatever it points at: the contents of another file in the checkout
            // reported as this path's content, or a file off the host for a link that leaves the checkout.
            return link.Target;
        }

        if (!File.Exists(candidatePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(candidatePath);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return await reader.ReadToEndAsync(ct);
        }
        catch (IOException ex)
        {
            // A single unreadable file (e.g. ENAMETOOLONG on a filename-encrypted or network-backed
            // workspace volume) must not abort the whole review. Treat it as content-unavailable; the
            // git-backed unified diff is still produced separately.
            logger.LogWarning(
                ex,
                "Failed to read file content from workspace at {CandidatePath}; treating as unavailable.",
                candidatePath);
            return null;
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
            try
            {
                Directory.Delete(worktreePath, true);
            }
            catch (Exception deleteException)
            {
                // Releasing a workspace runs on the way out of the pipeline, including on the way out of a
                // failure. Throwing here would replace the exception the caller is already carrying with one
                // about a directory, and the retention sweep reclaims what is left behind anyway.
                logger.LogWarning(
                    deleteException,
                    "Failed to delete worktree directory {WorktreePath}; leaving it to the retention sweep.",
                    worktreePath);
            }
        }
    }

    /// <summary>
    ///     What a path in the checkout is: the text of the link it names, or whether it leads through one.
    /// </summary>
    /// <remarks>
    ///     The containment check on the path itself is lexical, so it stops <c>../</c> in a requested path and
    ///     nothing else. A pull request can add a symbolic link pointing anywhere on the host, and a requested
    ///     path is whatever the review asked for rather than an entry of the tree, so a link the pull request
    ///     adds and a path below it are enough to reach a host file: the link resolves the directory part and
    ///     the request supplies the rest.
    ///     <para>
    ///         A tree holds no entries below a link, so a path that leads through one names nothing in the
    ///         revision and has no content to return. A link at the end of the path does name something,
    ///         which is the link itself.
    ///     </para>
    ///     <para>
    ///         Neither the link nor an absent path raises here: the target text is read from the link and not
    ///         from what it points at, so a link pointing outside the checkout, or at nothing, answers the
    ///         same as any other.
    ///     </para>
    /// </remarks>
    private static (string? Target, bool LeadsThroughALink) InspectLink(string candidatePath, string root, ILogger logger)
    {
        try
        {
            if (new FileInfo(candidatePath).LinkTarget is { } target)
            {
                return (target, false);
            }

            for (var directory = Path.GetDirectoryName(candidatePath);
                 directory is not null && directory.Length > root.Length;
                 directory = Path.GetDirectoryName(directory))
            {
                if (new DirectoryInfo(directory).LinkTarget is not { } directoryTarget)
                {
                    continue;
                }

                logger.LogWarning(
                    "Refused to read {CandidatePath}: {LinkPath} in it is a symbolic link to {LinkTarget}, "
                    + "so the path is not one the revision holds.",
                    candidatePath,
                    directory,
                    directoryTarget);
                return (null, true);
            }

            return (null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whether the path is or leads through a link is now unknown, and reading it could return
            // content from outside the revision.
            logger.LogWarning(
                ex,
                "Refused to read {CandidatePath}: the symbolic links on it could not be inspected.",
                candidatePath);
            return (null, true);
        }
    }

    /// <summary>
    ///     Splits one <c>ls-tree</c> record into what this class needs from it: whether the entry is a file,
    ///     and its path.
    /// </summary>
    /// <remarks>
    ///     A record is the mode, the type and the object name separated by spaces, then a tab, then the path.
    ///     The path is taken from the first tab onwards so that a filename containing a tab, a space, or
    ///     leading and trailing spaces arrives as the revision stores it.
    /// </remarks>
    private static (bool IsBlob, string Path) ParseTreeEntry(string record)
    {
        var separator = record.IndexOf('\t', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (false, string.Empty);
        }

        var fields = record[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var isBlob = fields.Length >= 2 && string.Equals(fields[1], "blob", StringComparison.Ordinal);
        return (isBlob, record[(separator + 1)..]);
    }

    private static bool IsTargetSide(string branchSide)
    {
        return string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    ///     Leading and trailing whitespace is kept. The file listing reports paths as the repository stores
    ///     them, so trimming here would turn a name the listing returned as " leading.txt" into a different
    ///     path, and the read of it would find nothing.
    /// </remarks>
    private static string NormalizePath(string path)
    {
        return path.TrimStart('/').Replace('\\', '/');
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
