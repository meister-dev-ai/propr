// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

[Collection("GitCommandLine")]
public sealed class GitReviewRepositoryWorkspaceDisposalTests
{
    /// <summary>
    ///     Release against a real mirror and a real linked worktree, so the git removal runs and succeeds.
    ///     The other cases here point at an empty directory, where that command cannot work and the direct
    ///     delete is what cleans up, so a regression in the normal path would not show in them.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithALinkedWorktree_RemovesItThroughGitAndReleasesTheLease()
    {
        var root = CreateRoot();
        var cleanupService = CreateCleanupService(root);
        var mirrorPath = Path.Combine(root, "mirrors", "real");
        var originPath = Path.Combine(root, "origin");
        Directory.CreateDirectory(mirrorPath);
        Directory.CreateDirectory(originPath);

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        try
        {
            await RunGitAsync(runner, originPath, ["init", "--initial-branch=main"]);
            await RunGitAsync(runner, originPath, ["config", "user.email", "tests@example.invalid"]);
            await RunGitAsync(runner, originPath, ["config", "user.name", "Tests"]);
            await RunGitAsync(runner, originPath, ["config", "commit.gpgsign", "false"]);
            await File.WriteAllTextAsync(Path.Combine(originPath, "file.txt"), "content\n");
            await RunGitAsync(runner, originPath, ["add", "-A"]);
            await RunGitAsync(runner, originPath, ["commit", "-m", "one"]);
            var head = await RunGitAsync(runner, originPath, ["rev-parse", "HEAD"]);

            await RunGitAsync(runner, mirrorPath, ["init", "--bare"]);
            await RunGitAsync(runner, mirrorPath, ["remote", "add", "origin", originPath]);
            await RunGitAsync(runner, mirrorPath, ["fetch", "origin", "+refs/heads/*:refs/remotes/origin/*"]);

            var headPath = Path.Combine(root, "workspaces", "real", "source");
            Directory.CreateDirectory(Path.GetDirectoryName(headPath)!);
            await RunGitAsync(runner, mirrorPath, ["worktree", "add", "--detach", "--force", headPath, head.Trim()]);

            var lease = new ReviewRepositoryWorkspaceLease(
                Guid.NewGuid(),
                "real",
                mirrorPath,
                headPath,
                head.Trim(),
                head.Trim(),
                head.Trim(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Active");
            cleanupService.RegisterLease(lease);

            await CreateWorkspace(lease, cleanupService).DisposeAsync();

            Assert.False(cleanupService.IsMirrorReferenced(mirrorPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(headPath)));

            // The mirror no longer lists the worktree, which is what distinguishes a git removal from a
            // directory that was deleted underneath it.
            var registered = await RunGitAsync(runner, mirrorPath, ["worktree", "list"]);
            Assert.DoesNotContain("source", registered, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ReleasesTheLeaseOnce()
    {
        // The reference count is what cleanup consults before deleting a mirror, so a second release from
        // one job would report a shared mirror as unused while another job still holds it. This asserts the
        // count only; that cleanup then deletes such a mirror is not exercised here.
        var root = CreateRoot();
        var cleanupService = CreateCleanupService(root);
        var mirrorPath = Path.Combine(root, "mirrors", "shared");
        Directory.CreateDirectory(mirrorPath);

        var firstLease = CreateLease(root, mirrorPath, "first");
        var secondLease = CreateLease(root, mirrorPath, "second");
        cleanupService.RegisterLease(firstLease);
        cleanupService.RegisterLease(secondLease);

        var workspace = CreateWorkspace(firstLease, cleanupService);

        try
        {
            await workspace.DisposeAsync();
            await workspace.DisposeAsync();

            Assert.True(
                cleanupService.IsMirrorReferenced(mirrorPath),
                "the second job's lease still holds the mirror");

            cleanupService.ReleaseLease(secondLease);
            Assert.False(cleanupService.IsMirrorReferenced(mirrorPath));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheLeaseAndDeletesTheWorkspace()
    {
        var root = CreateRoot();
        var cleanupService = CreateCleanupService(root);
        var mirrorPath = Path.Combine(root, "mirrors", "only");
        Directory.CreateDirectory(mirrorPath);

        var lease = CreateLease(root, mirrorPath, "only");
        cleanupService.RegisterLease(lease);
        var workspace = CreateWorkspace(lease, cleanupService);

        try
        {
            await workspace.DisposeAsync();

            Assert.False(cleanupService.IsMirrorReferenced(mirrorPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(lease.HeadWorkspacePath)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> RunGitAsync(GitCommandRunner runner, string workingDirectory, string[] arguments)
    {
        var result = await runner.RunAsync(workingDirectory, arguments, null, CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private static GitReviewRepositoryWorkspace CreateWorkspace(
        ReviewRepositoryWorkspaceLease lease,
        ReviewWorkspaceCleanupService cleanupService)
    {
        return new GitReviewRepositoryWorkspace(
            lease,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger.Instance,
            cleanupService);
    }

    private static ReviewWorkspaceCleanupService CreateCleanupService(string root)
    {
        return new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = root }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);
    }

    private static ReviewRepositoryWorkspaceLease CreateLease(string root, string mirrorPath, string key)
    {
        var headPath = Path.Combine(root, "workspaces", key, "source");
        Directory.CreateDirectory(headPath);
        return new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            key,
            mirrorPath,
            headPath,
            "head-sha",
            "base-sha",
            "merge-base-sha",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-disposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch (IOException)
        {
            // A directory left under the temp path does not affect any assertion here, so a failed
            // delete is ignored.
        }
    }
}
