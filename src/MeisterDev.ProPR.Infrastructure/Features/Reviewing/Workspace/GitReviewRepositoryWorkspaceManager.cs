// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class GitReviewRepositoryWorkspaceManager(
    IOptions<ReviewWorkspaceOptions> options,
    IReviewWorkspaceRemoteResolver remoteResolver,
    GitCommandRunner gitCommandRunner,
    ReviewWorkspaceCleanupService cleanupService,
    ReviewWorkspacePreparationThrottle preparationThrottle,
    ILogger<GitReviewRepositoryWorkspaceManager> logger) : IReviewRepositoryWorkspaceManager
{
    /// <summary>
    ///     Packfile count at which a mirror is repacked. Matches git's own <c>gc.autoPackLimit</c> default, so
    ///     this only ever fires where git's automatic maintenance was expected to and did not.
    /// </summary>
    private const int MaxPackFilesBeforeRepack = 50;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MirrorLocks = new(StringComparer.Ordinal);

    public async Task<ReviewRepositoryWorkspacePreparationResult> PrepareAsync(
        ReviewRepositoryWorkspaceRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            Directory.CreateDirectory(options.Value.RootPath);
            await cleanupService.RunCleanupAsync(ct);

            var remote = await remoteResolver.ResolveAsync(request, ct);
            if (!remote.SupportsLocalFetch)
            {
                return Fail("remote_resolution", "unsupported_auth_mode", "The configured SCM authentication mode does not support local git fetch.", false);
            }

            // The throttle bounds simultaneous checkouts across all repositories; the mirror lock keeps two
            // preparations out of one mirror. Always in this order, so the per-repository lock is never held
            // while waiting for a slot.
            using var preparationSlot = await preparationThrottle.EnterAsync(ct);
            var mirrorLock = MirrorLocks.GetOrAdd(remote.RepositoryKey, _ => new SemaphoreSlim(1, 1));
            await mirrorLock.WaitAsync(ct);
            try
            {
                var lease = await this.PrepareWorkspaceAsync(request, remote, ct);
                cleanupService.RegisterLease(lease);
                var workspace = new GitReviewRepositoryWorkspace(
                    lease,
                    gitCommandRunner,
                    logger,
                    cleanupService,
                    BuildAuthEnvironment(remote.AuthorizationHeader));
                return new ReviewRepositoryWorkspacePreparationResult(workspace, null);
            }
            finally
            {
                mirrorLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to prepare local review workspace for job {JobId}.", request.JobId);
            return Fail("preparation", "workspace_prepare_failed", ex.Message, true);
        }
    }

    private async Task<ReviewRepositoryWorkspaceLease> PrepareWorkspaceAsync(
        ReviewRepositoryWorkspaceRequest request,
        ReviewWorkspaceRemoteRef remote,
        CancellationToken ct)
    {
        var mirrorsRoot = Path.Combine(options.Value.RootPath, "mirrors");
        var workspacesRoot = Path.Combine(options.Value.RootPath, "workspaces");
        Directory.CreateDirectory(mirrorsRoot);
        Directory.CreateDirectory(workspacesRoot);

        var mirrorPath = Path.Combine(mirrorsRoot, ComputeStableKey(remote.RepositoryKey));
        if (!Directory.Exists(mirrorPath))
        {
            Directory.CreateDirectory(mirrorPath);
            var initResult = await gitCommandRunner.RunAsync(
                mirrorPath,
                ["init", "--bare"],
                null,
                ct);
            initResult.EnsureSuccess("init bare mirror", "git init --bare");
        }

        var authEnvironment = BuildAuthEnvironment(remote.AuthorizationHeader);
        var pruneResult = await gitCommandRunner.RunAsync(mirrorPath, ["worktree", "prune"], null, ct);
        pruneResult.EnsureSuccess("prune worktrees", "git worktree prune");

        // The URL is updated in place rather than the remote being removed and added again. Git records a
        // mirror's partial-clone state on the remote — the filter it was fetched with, and whether it may
        // fetch missing objects on demand — so removing the remote discards it. The policy alignment below
        // reads that state to decide whether this mirror has to be told to bring down the contents an
        // earlier filtered fetch omitted, and would see a mirror that had never been filtered. The mirror
        // would then keep its missing objects with nothing left to fetch them from.
        var setUrlResult = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["remote", "set-url", "origin", remote.RemoteUrl],
            authEnvironment,
            ct);
        if (setUrlResult.ExitCode != 0)
        {
            // No such remote yet, which is the case for a mirror this preparation just created.
            var addRemoteResult = await gitCommandRunner.RunAsync(
                mirrorPath,
                ["remote", "add", "origin", remote.RemoteUrl],
                authEnvironment,
                ct);
            addRemoteResult.EnsureSuccess("add remote", "git remote add origin <remote>");
        }

        var leftPartialClone = await this.AlignMirrorWithPolicyAsync(mirrorPath, remote, authEnvironment, ct);
        var fetchArguments = this.BuildFetchArguments(remote, leftPartialClone);
        var fetchResult = await gitCommandRunner.RunAsync(mirrorPath, fetchArguments, authEnvironment, ct);

        // The policy is named in the failure because it decides what the fetch asked the server for, and a
        // server that will not serve a filtered fetch fails here. Its value is one of a fixed set, unlike the
        // refspecs, which come from the provider and are left out of the message for that reason.
        fetchResult.EnsureSuccess(
            "fetch mirror",
            $"git fetch --prune [{options.Value.FetchDepthPolicy}] origin <refspecs>");

        if (leftPartialClone)
        {
            // Every object is present now, so the mirror no longer needs a promisor remote. Leaving it set
            // would keep reads able to reach the server for objects, which is what the policy just stopped.
            var unsetPromisorResult = await gitCommandRunner.RunAsync(
                mirrorPath,
                ["config", "--local", "--unset-all", "remote.origin.promisor"],
                authEnvironment,
                ct);

            // Exit code 5 is "no such key", which is the normal answer for a filtered mirror whose promisor
            // setting was already gone. Anything else left the mirror marked as a promisor repository, so it
            // is reported: the objects are all present, and reads will not need the server, but the mirror
            // does not match the policy and the next preparation will not try this again.
            if (unsetPromisorResult.ExitCode is not (0 or 5))
            {
                logger.LogWarning(
                    "Mirror {MirrorPath} left the blobless policy but remote.origin.promisor could not be cleared (exit code {ExitCode}): {Error}",
                    mirrorPath,
                    unsetPromisorResult.ExitCode,
                    unsetPromisorResult.StandardError.Trim());
            }
        }

        await this.RepackMirrorIfNeededAsync(mirrorPath, authEnvironment, ct);

        await this.EnsureCommitPresentAsync(mirrorPath, request.ReviewRevision.HeadSha, ct);
        await this.EnsureCommitPresentAsync(mirrorPath, request.ReviewRevision.BaseSha, ct);

        var mergeBaseSha = !string.IsNullOrWhiteSpace(request.ReviewRevision.StartSha)
            ? request.ReviewRevision.StartSha!
            : await this.ResolveMergeBaseAsync(mirrorPath, request.ReviewRevision.BaseSha, request.ReviewRevision.HeadSha, ct);

        // The job id is part of the key so two jobs reviewing the same revision pair cannot resolve to
        // the same checkout. Preparation deletes the directory unconditionally below, and supersede/retry
        // churn regularly puts several jobs on one revision: without the job id that delete removes the
        // worktrees of a review that is still running, which surfaces mid-review as
        // "fatal: not a git repository" and "Could not write new index file". With it, the delete can only
        // ever remove a previous attempt of this same job.
        var workspaceKey = ComputeStableKey($"{remote.RepositoryKey}:{request.ReviewRevision.BaseSha}:{request.ReviewRevision.HeadSha}:{request.JobId}");
        var workspaceRoot = Path.Combine(workspacesRoot, workspaceKey);
        if (Directory.Exists(workspaceRoot))
        {
            Directory.Delete(workspaceRoot, true);
        }

        Directory.CreateDirectory(workspaceRoot);
        var headWorkspacePath = Path.Combine(workspaceRoot, "source");

        // The workspace key is deterministic, so the same path can have been registered by an earlier
        // attempt of this job whose worktree directory has since vanished — a partial cleanup or
        // ephemeral container storage dropping the dir while the persistent mirror keeps the
        // registration. Git then rejects 'worktree add' with "missing but already registered".
        // Prune the mirror's stale registrations before (re)creating the worktree here.
        await this.PruneWorktreesAsync(mirrorPath, ct);

        // Only the head revision is checked out. The target side of the review is read from the object store
        // at the base commit, so a second checkout would write a second full copy of the repository to the
        // workspace disk for reads that never used it.
        await this.CreateWorktreeAsync(mirrorPath, headWorkspacePath, request.ReviewRevision.HeadSha, authEnvironment, ct);

        var preparedAt = DateTimeOffset.UtcNow;
        return new ReviewRepositoryWorkspaceLease(
            request.JobId,
            workspaceKey,
            mirrorPath,
            headWorkspacePath,
            request.ReviewRevision.HeadSha,
            request.ReviewRevision.BaseSha,
            mergeBaseSha,
            preparedAt,
            preparedAt,
            "Active");
    }

    /// <param name="mirrorPath">The mirror the worktree is added to.</param>
    /// <param name="worktreePath">Where the checkout is written.</param>
    /// <param name="commitSha">The commit to check out.</param>
    /// <param name="authEnvironment">
    ///     Credentials for the mirror's remote. A checkout from a partial clone downloads the file contents it
    ///     needs as it writes them, and that download is authenticated like any other fetch.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    private async Task CreateWorktreeAsync(
        string mirrorPath,
        string worktreePath,
        string commitSha,
        IReadOnlyDictionary<string, string?>? authEnvironment,
        CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["worktree", "add", "--detach", "--force", worktreePath, commitSha],
            authEnvironment,
            ct);
        result.EnsureSuccess("create worktree", "git worktree add --detach --force <path> <sha>");
    }

    /// <summary>
    ///     Undoes a narrower policy a mirror was fetched under previously, so the configured one applies to
    ///     it. Returns whether the mirror has just stopped being a partial clone.
    /// </summary>
    /// <remarks>
    ///     A mirror outlives the setting. One fetched as shallow or as a partial clone stays that way,
    ///     because git records the shallow boundary and the filter in the repository, and neither is undone
    ///     by simply asking for more on the next fetch: a boundary keeps limiting merge-base resolution, and
    ///     a filter keeps being applied to every later fetch.
    ///     <para>
    ///         The boundary is removed by its own fetch. Passing <c>--unshallow</c> and <c>--refetch</c> in
    ///         one command deepens the history and transfers no file contents, which would leave a mirror
    ///         that reports the right history and is still missing every blob.
    ///     </para>
    /// </remarks>
    private async Task<bool> AlignMirrorWithPolicyAsync(
        string mirrorPath,
        ReviewWorkspaceRemoteRef remote,
        IReadOnlyDictionary<string, string?>? authEnvironment,
        CancellationToken ct)
    {
        var policy = options.Value.FetchDepthPolicy;
        var isShallowPolicy = string.Equals(policy, ReviewWorkspaceFetchDepthPolicies.Shallow, StringComparison.OrdinalIgnoreCase);
        var isBloblessPolicy = string.Equals(policy, ReviewWorkspaceFetchDepthPolicies.Blobless, StringComparison.OrdinalIgnoreCase);

        if (!isShallowPolicy && File.Exists(Path.Combine(mirrorPath, "shallow")))
        {
            var unshallowArguments = new List<string> { "fetch", "--prune", "--unshallow" };

            // Under the blobless policy this fetch carries the filter as well. Without it the deepening
            // transfers the file contents of the whole history, and the filter on the fetch that follows
            // cannot give that space back.
            if (isBloblessPolicy)
            {
                unshallowArguments.Add("--filter=blob:none");
            }

            unshallowArguments.Add("origin");
            unshallowArguments.AddRange(remote.FetchRefSpecs);
            var unshallowResult = await gitCommandRunner.RunAsync(mirrorPath, unshallowArguments, authEnvironment, ct);
            unshallowResult.EnsureSuccess("remove the mirror's shallow boundary", "git fetch --prune --unshallow origin <refspecs>");
        }

        if (isBloblessPolicy)
        {
            return false;
        }

        // A filter recorded on the remote is applied to every later fetch, so it is removed before the fetch
        // this preparation runs. Removing it does not bring down what an earlier filtered fetch omitted: the
        // commits are already present, so an ordinary fetch transfers nothing and the mirror keeps reaching
        // the server for file contents. The caller passes --refetch for that, and drops the promisor setting
        // once it has succeeded.
        var filter = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["config", "--local", "--get", "remote.origin.partialclonefilter"],
            authEnvironment,
            ct);
        if (filter.ExitCode != 0 || string.IsNullOrWhiteSpace(filter.StandardOutput))
        {
            // Exit code 1 is "no such key", which is the normal case for a mirror that was never fetched
            // under the blobless policy.
            return false;
        }

        var unsetFilterResult = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["config", "--local", "--unset-all", "remote.origin.partialclonefilter"],
            authEnvironment,
            ct);
        unsetFilterResult.EnsureSuccess("clear the mirror's partial clone filter", "git config --unset-all remote.origin.partialclonefilter");
        return true;
    }

    /// <summary>Builds the fetch for the configured depth policy.</summary>
    /// <param name="remote">The resolved remote, for its refspecs.</param>
    /// <param name="refetch">
    ///     Whether to ask for the objects an earlier filtered fetch omitted. Set when the mirror has just
    ///     stopped being a partial clone, where an ordinary fetch would transfer nothing.
    /// </param>
    private List<string> BuildFetchArguments(ReviewWorkspaceRemoteRef remote, bool refetch)
    {
        var arguments = new List<string> { "fetch", "--prune" };
        var policy = options.Value.FetchDepthPolicy;

        if (refetch)
        {
            arguments.Add("--refetch");
        }

        if (string.Equals(policy, ReviewWorkspaceFetchDepthPolicies.Blobless, StringComparison.OrdinalIgnoreCase))
        {
            // Commits and trees only; file contents are downloaded when something reads them. A review
            // reads a small fraction of a large repository, so most of the fetched bytes were never used.
            arguments.Add("--filter=blob:none");
        }
        else if (string.Equals(policy, ReviewWorkspaceFetchDepthPolicies.Shallow, StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add($"--depth={options.Value.FetchDepth}");
        }

        arguments.Add("origin");
        arguments.AddRange(remote.FetchRefSpecs);
        return arguments;
    }

    /// <summary>
    ///     Consolidates a mirror's packfiles once there are enough of them to matter.
    /// </summary>
    /// <remarks>
    ///     Every job preparation fetches into the mirror and every fetch writes another packfile. Git
    ///     resolves an object by consulting one index per pack, so the cost of the object reads behind the
    ///     per-file review path (the changed-file listing and one unified diff per file) grows with the pack
    ///     count. Git's own automatic maintenance would normally cap it, but it cannot finish on a full disk
    ///     and records the failure in <c>gc.log</c>, which then suppresses further attempts for a day, so a
    ///     mirror on a disk that has run out of space keeps accumulating packs indefinitely.
    ///     <para>
    ///         Two things must be true before repacking. The caller holds this repository's mirror lock, so
    ///         no other preparation is fetching into it, and the mirror must hold no lease, so no review is
    ///         reading it. Repacking writes the new pack before removing the old ones, so the only exposure
    ///         for a concurrent reader is a pack list that has gone stale mid-read. Waiting for the mirror to
    ///         be unreferenced removes that exposure, and costs only a deferral to the next preparation.
    ///     </para>
    /// </remarks>
    private async Task RepackMirrorIfNeededAsync(
        string mirrorPath,
        IReadOnlyDictionary<string, string?>? authEnvironment,
        CancellationToken ct)
    {
        var packDirectory = Path.Combine(mirrorPath, "objects", "pack");
        if (!Directory.Exists(packDirectory))
        {
            return;
        }

        var packCount = Directory.EnumerateFiles(packDirectory, "*.pack").Count();
        if (packCount <= MaxPackFilesBeforeRepack)
        {
            return;
        }

        if (cleanupService.IsMirrorReferenced(mirrorPath))
        {
            logger.LogDebug(
                "Mirror {MirrorPath} has {PackCount} packfiles but is in use by a running review; leaving the repack to a later preparation.",
                mirrorPath,
                packCount);
            return;
        }

        var result = await gitCommandRunner.RunAsync(mirrorPath, ["repack", "-adq"], authEnvironment, ct);
        if (result.ExitCode != 0)
        {
            // Logged and not thrown: a mirror that was not repacked resolves objects more slowly and still
            // resolves them correctly, so the review it was preparing for continues.
            logger.LogWarning(
                "Repacking mirror {MirrorPath} failed with exit code {ExitCode}: {Error}",
                mirrorPath,
                result.ExitCode,
                result.StandardError.Trim());
            return;
        }

        RemoveStaleMaintenanceLog(mirrorPath, logger);

        logger.LogInformation(
            "Repacked mirror {MirrorPath}: {PackCountBefore} packfiles before, {PackCountAfter} after.",
            mirrorPath,
            packCount,
            Directory.Exists(packDirectory) ? Directory.EnumerateFiles(packDirectory, "*.pack").Count() : 0);
    }

    /// <summary>
    ///     Removes the record git writes when its own automatic maintenance fails. While the file is present
    ///     git skips automatic maintenance for a day, and the repack that just succeeded has done the work
    ///     the failed attempt was trying to do.
    /// </summary>
    private static void RemoveStaleMaintenanceLog(string mirrorPath, ILogger logger)
    {
        var maintenanceLogPath = Path.Combine(mirrorPath, "gc.log");
        try
        {
            File.Delete(maintenanceLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to delete {MaintenanceLogPath} after repacking the mirror.", maintenanceLogPath);
        }
    }

    private async Task PruneWorktreesAsync(string mirrorPath, CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["worktree", "prune"],
            null,
            ct);
        result.EnsureSuccess("prune worktrees", "git worktree prune");
    }

    private async Task EnsureCommitPresentAsync(string mirrorPath, string commitSha, CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["rev-parse", "--verify", $"{commitSha}^{{commit}}"],
            null,
            ct);
        result.EnsureSuccess("verify commit", "git rev-parse --verify <sha>^{commit}");
    }

    private async Task<string> ResolveMergeBaseAsync(string mirrorPath, string baseSha, string headSha, CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["merge-base", baseSha, headSha],
            null,
            ct);
        result.EnsureSuccess("resolve merge-base", "git merge-base <base> <head>");
        return result.StandardOutput.Trim();
    }

    private static ReviewRepositoryWorkspacePreparationResult Fail(string stage, string code, string message, bool retryable)
    {
        return new ReviewRepositoryWorkspacePreparationResult(null, new ReviewWorkspaceFailure(stage, code, message, retryable, false));
    }

    private static string ComputeStableKey(string value)
    {
        // Use a short, fixed-length prefix of the hash. The key only needs to be collision-free
        // across the (small, ephemeral) set of active workspaces/mirrors, so 64 bits is ample.
        // A short component also keeps the full checkout path well within the lower path-length
        // limits of constrained container storage (e.g. Azure Container Instances), where a full
        // 64-char SHA-256 component pushes deep repo paths past the effective limit and surfaces
        // as ENAMETOOLONG/PathTooLongException on open.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string?>? BuildAuthEnvironment(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        return new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = authorizationHeader,
        };
    }
}
