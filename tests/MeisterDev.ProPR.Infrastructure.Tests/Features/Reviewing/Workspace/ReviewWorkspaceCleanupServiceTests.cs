// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

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

    /// <summary>
    ///     Mirror sizes are measured with a cache now, keyed on each directory's own timestamp, so a sweep
    ///     has to keep working after the first one has populated it — including for mirrors that appeared,
    ///     grew or were removed in between.
    /// </summary>
    [Fact]
    public async Task RunCleanupAsync_RepeatedSweeps_KeepMeasuringAChangingMirrorSet()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        var mirrorsRoot = Path.Combine(root, "mirrors");
        var packDirectory = Path.Combine(mirrorsRoot, "mirror-a", "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        await File.WriteAllTextAsync(Path.Combine(packDirectory, "pack-1.pack"), new string('x', 512));

        var workspacesRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspacesRoot);

        var sut = new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = root, RetentionMinutes = 1 }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);

        try
        {
            await sut.RunCleanupAsync(CancellationToken.None);

            // A second mirror, another packfile in the first, and one expired workspace: the sweep after
            // this has to see all three.
            await File.WriteAllTextAsync(Path.Combine(packDirectory, "pack-2.pack"), new string('x', 512));
            Directory.CreateDirectory(Path.Combine(mirrorsRoot, "mirror-b"));
            var expiredWorkspace = Path.Combine(workspacesRoot, "expired");
            Directory.CreateDirectory(expiredWorkspace);
            Directory.SetLastWriteTimeUtc(expiredWorkspace, DateTime.UtcNow.AddHours(-4));

            await sut.RunCleanupAsync(CancellationToken.None);

            Assert.False(Directory.Exists(expiredWorkspace));
            Assert.True(Directory.Exists(packDirectory));

            // A mirror that disappears between sweeps is measured no more and fails nothing.
            Directory.Delete(Path.Combine(mirrorsRoot, "mirror-b"), true);
            await sut.RunCleanupAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>
    ///     A mirror nobody has fetched into within the retention period is removed even though the cache is
    ///     far inside its size budget, which the size rule alone would never do.
    /// </summary>
    [Fact]
    public async Task RunCleanupAsync_DeletesAMirrorOlderThanTheRetentionPeriod()
    {
        var root = CreateRoot();
        var mirror = CreateMirror(root, "stale", DateTime.UtcNow.AddDays(-9));

        var sut = CreateService(root, mirrorRetentionDays: 7);

        try
        {
            await sut.RunCleanupAsync(CancellationToken.None);
            Assert.False(Directory.Exists(mirror));
        }
        finally
        {
            TryDeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunCleanupAsync_KeepsAMirrorYoungerThanTheRetentionPeriod()
    {
        var root = CreateRoot();
        var mirror = CreateMirror(root, "recent", DateTime.UtcNow.AddDays(-2));

        var sut = CreateService(root, mirrorRetentionDays: 7);

        try
        {
            await sut.RunCleanupAsync(CancellationToken.None);
            Assert.True(Directory.Exists(mirror));
        }
        finally
        {
            TryDeleteRoot(root);
        }
    }

    // A review is reading the mirror, and removing it would fail that review with a git error naming a
    // repository that is gone. The sweep after the review releases it weighs its age again.
    [Fact]
    public async Task RunCleanupAsync_KeepsAnOldMirrorARunningReviewHolds()
    {
        var root = CreateRoot();
        var mirror = CreateMirror(root, "held", DateTime.UtcNow.AddDays(-9));

        var sut = CreateService(root, mirrorRetentionDays: 7);
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "workspace-key",
            mirror,
            Path.Combine(root, "workspaces", "active", "source"),
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
            Assert.True(Directory.Exists(mirror));
        }
        finally
        {
            sut.ReleaseLease(lease);
            TryDeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void MirrorRetentionDays_OutsideItsRange_FailsValidation(int days)
    {
        var options = new ReviewWorkspaceOptions { MirrorRetentionDays = days };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ReviewWorkspaceOptions.MirrorRetentionDays)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(365)]
    public void MirrorRetentionDays_InsideItsRange_PassesValidation(int days)
    {
        var options = new ReviewWorkspaceOptions { MirrorRetentionDays = days };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.DoesNotContain(results, result => result.MemberNames.Contains(nameof(ReviewWorkspaceOptions.MirrorRetentionDays)));
    }

    [Fact]
    public async Task RunCleanupAsync_DoesNotDeleteReferencedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(root, "workspaces", "active");
        var headPath = Path.Combine(workspaceRoot, "source");
        Directory.CreateDirectory(headPath);
        Directory.SetLastWriteTimeUtc(workspaceRoot, DateTime.UtcNow.AddHours(-4));

        var sut = new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = root, RetentionMinutes = 1 }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "workspace-key",
            Path.Combine(root, "mirrors", "mirror"),
            headPath,
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

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mirrors"));
        Directory.CreateDirectory(Path.Combine(root, "workspaces"));
        return root;
    }

    /// <summary>
    ///     A mirror directory holding one packfile, dated as a fetch of that age would have left it.
    /// </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="name">The mirror directory's name.</param>
    /// <param name="lastWriteTimeUtc">When the last fetch into it wrote.</param>
    private static string CreateMirror(string root, string name, DateTime lastWriteTimeUtc)
    {
        var mirror = Path.Combine(root, "mirrors", name);
        var packDirectory = Path.Combine(mirror, "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(Path.Combine(packDirectory, "pack-1.pack"), new string('x', 512));
        Directory.SetLastWriteTimeUtc(mirror, lastWriteTimeUtc);
        return mirror;
    }

    private static ReviewWorkspaceCleanupService CreateService(string root, int mirrorRetentionDays)
    {
        return new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(
                new ReviewWorkspaceOptions
                {
                    RootPath = root,
                    RetentionMinutes = 1,
                    MirrorRetentionDays = mirrorRetentionDays,
                }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);
    }

    private static void TryDeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
