// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

public sealed class ReviewWorkspaceCleanupServiceTests
{
    [Fact]
    public async Task RunCleanupAsync_DeletesReleasedExpiredWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspacesRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspacesRoot);
        var expiredWorkspace = Path.Combine(workspacesRoot, "expired");
        Directory.CreateDirectory(expiredWorkspace);
        Directory.SetLastWriteTimeUtc(expiredWorkspace, DateTime.UtcNow.AddHours(-4));

        var sut = new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = root, RetentionMinutes = 1 }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);

        try
        {
            await sut.RunCleanupAsync(CancellationToken.None);
            Assert.False(Directory.Exists(expiredWorkspace));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task RunCleanupAsync_DoesNotDeleteReferencedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(root, "workspaces", "active");
        var headPath = Path.Combine(workspaceRoot, "source");
        var basePath = Path.Combine(workspaceRoot, "target");
        Directory.CreateDirectory(headPath);
        Directory.CreateDirectory(basePath);
        Directory.SetLastWriteTimeUtc(workspaceRoot, DateTime.UtcNow.AddHours(-4));

        var sut = new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = root, RetentionMinutes = 1 }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "workspace-key",
            Path.Combine(root, "mirrors", "mirror"),
            headPath,
            basePath,
            "head-sha",
            "base-sha",
            "merge-base",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");

        try
        {
            sut.RegisterLease(lease);
            await sut.RunCleanupAsync(CancellationToken.None);
            Assert.True(Directory.Exists(workspaceRoot));
        }
        finally
        {
            sut.ReleaseLease(lease);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
