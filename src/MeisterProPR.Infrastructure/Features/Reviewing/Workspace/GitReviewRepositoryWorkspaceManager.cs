// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class GitReviewRepositoryWorkspaceManager(
    IOptions<ReviewWorkspaceOptions> options,
    IReviewWorkspaceRemoteResolver remoteResolver,
    GitCommandRunner gitCommandRunner,
    ReviewWorkspaceCleanupService cleanupService,
    ILogger<GitReviewRepositoryWorkspaceManager> logger) : IReviewRepositoryWorkspaceManager
{
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

            var mirrorLock = MirrorLocks.GetOrAdd(remote.RepositoryKey, _ => new SemaphoreSlim(1, 1));
            await mirrorLock.WaitAsync(ct);
            try
            {
                var lease = await this.PrepareWorkspaceAsync(request, remote, ct);
                cleanupService.RegisterLease(lease);
                var workspace = new GitReviewRepositoryWorkspace(lease, gitCommandRunner, logger, cleanupService);
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

        var remoteSetResult = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["remote", "remove", "origin"],
            authEnvironment,
            ct);
        if (remoteSetResult.ExitCode != 0)
        {
            _ = remoteSetResult;
        }

        var addRemoteResult = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["remote", "add", "origin", remote.RemoteUrl],
            authEnvironment,
            ct);
        addRemoteResult.EnsureSuccess("add remote", "git remote add origin <remote>");

        var fetchArguments = new List<string> { "fetch", "--prune", "origin" };
        fetchArguments.AddRange(remote.FetchRefSpecs);
        var fetchResult = await gitCommandRunner.RunAsync(mirrorPath, fetchArguments, authEnvironment, ct);
        fetchResult.EnsureSuccess("fetch mirror", "git fetch --prune origin <refspecs>");

        await this.EnsureCommitPresentAsync(mirrorPath, request.ReviewRevision.HeadSha, ct);
        await this.EnsureCommitPresentAsync(mirrorPath, request.ReviewRevision.BaseSha, ct);

        var mergeBaseSha = !string.IsNullOrWhiteSpace(request.ReviewRevision.StartSha)
            ? request.ReviewRevision.StartSha!
            : await this.ResolveMergeBaseAsync(mirrorPath, request.ReviewRevision.BaseSha, request.ReviewRevision.HeadSha, ct);

        var workspaceKey = ComputeStableKey($"{remote.RepositoryKey}:{request.ReviewRevision.BaseSha}:{request.ReviewRevision.HeadSha}");
        var workspaceRoot = Path.Combine(workspacesRoot, workspaceKey);
        if (Directory.Exists(workspaceRoot))
        {
            Directory.Delete(workspaceRoot, true);
        }

        Directory.CreateDirectory(workspaceRoot);
        var headWorkspacePath = Path.Combine(workspaceRoot, "source");
        var baseWorkspacePath = Path.Combine(workspaceRoot, "target");

        await this.CreateWorktreeAsync(mirrorPath, headWorkspacePath, request.ReviewRevision.HeadSha, ct);
        await this.CreateWorktreeAsync(mirrorPath, baseWorkspacePath, request.ReviewRevision.BaseSha, ct);

        var preparedAt = DateTimeOffset.UtcNow;
        return new ReviewRepositoryWorkspaceLease(
            request.JobId,
            workspaceKey,
            mirrorPath,
            headWorkspacePath,
            baseWorkspacePath,
            request.ReviewRevision.HeadSha,
            request.ReviewRevision.BaseSha,
            mergeBaseSha,
            preparedAt,
            preparedAt,
            "Active");
    }

    private async Task CreateWorktreeAsync(string mirrorPath, string worktreePath, string commitSha, CancellationToken ct)
    {
        var result = await gitCommandRunner.RunAsync(
            mirrorPath,
            ["worktree", "add", "--detach", worktreePath, commitSha],
            null,
            ct);
        result.EnsureSuccess("create worktree", "git worktree add --detach <path> <sha>");
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
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
