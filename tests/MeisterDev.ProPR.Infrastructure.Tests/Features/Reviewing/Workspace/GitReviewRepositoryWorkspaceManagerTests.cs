// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

/// <summary>
///     Workspace preparation against a real git repository. These tests assert git's behaviour: which
///     revisions are materialised, what the object store returns for a revision that is not checked out, and
///     how many packfiles a mirror accumulates. Replacing git with a double would leave nothing to assert.
/// </summary>
[Collection("GitCommandLine")]
public sealed class GitReviewRepositoryWorkspaceManagerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-workspace-manager-" + Guid.NewGuid().ToString("N"));

    private string _originPath = null!;
    private string _headSha = null!;
    private string _baseSha = null!;

    private string WorkspacesRoot => Path.Combine(this._root, "workspaces");

    private string MirrorsRoot => Path.Combine(this._root, "mirrors");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(this._root);
        this._originPath = Path.Combine(this._root, "origin");
        Directory.CreateDirectory(this._originPath);

        // A two-commit history on one branch: the base commit has kept.txt and changed.txt, the head commit
        // edits changed.txt and adds added.txt. That is enough to tell the two sides of a review apart.
        await GitAsync(this._originPath, "init", "--initial-branch=main");
        await GitAsync(this._originPath, "config", "user.email", "tests@example.invalid");
        await GitAsync(this._originPath, "config", "user.name", "Tests");
        await GitAsync(this._originPath, "config", "commit.gpgsign", "false");

        // Serving a filtered fetch is opt-in for a local repository, and the blobless policy needs it.
        await GitAsync(this._originPath, "config", "uploadpack.allowFilter", "true");

        await File.WriteAllTextAsync(Path.Combine(this._originPath, "kept.txt"), "kept\n");
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "changed.txt"), "before\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "base");
        this._baseSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");

        await File.WriteAllTextAsync(Path.Combine(this._originPath, "changed.txt"), "after\n");
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "added.txt"), "added\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "head");
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");
    }

    public Task DisposeAsync()
    {
        TryDeleteRoot(this._root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PrepareAsync_ForTwoJobsOnOneRevision_GivesEachItsOwnCheckout()
    {
        // Two jobs on the same revision pair used to resolve to one workspace directory, and preparing the
        // second deleted the first job's checkout from under a review that was still reading it.
        var manager = this.CreateManager();
        var firstJob = Guid.NewGuid();
        var secondJob = Guid.NewGuid();

        var first = await manager.PrepareAsync(this.CreateRequest(firstJob), CancellationToken.None);
        var second = await manager.PrepareAsync(this.CreateRequest(secondJob), CancellationToken.None);

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.NotEqual(first.Workspace!.Lease.WorkspaceKey, second.Workspace!.Lease.WorkspaceKey);

        // The first job's checkout survived the second job's preparation and is still readable.
        Assert.True(Directory.Exists(first.Workspace.Lease.HeadWorkspacePath));
        Assert.Equal("after\n", await first.Workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Source, CancellationToken.None));
        Assert.Equal(2, Directory.GetDirectories(this.WorkspacesRoot).Length);

        await first.Workspace.DisposeAsync();
        await second.Workspace.DisposeAsync();
    }

    /// <summary>
    ///     Four jobs on one repository, prepared at once. Preparation fetches into a shared mirror and adds a
    ///     worktree to it, and git serialises neither for us: two fetches into one repository contend for its
    ///     refs and two worktree additions for its administrative files, so the mirror lock is what makes
    ///     this work. The sequential cases cannot show that.
    ///     <para>
    ///         What it asserts is the outcome, so it detects the loss of that lock only when the timing
    ///         happens to collide: removing the lock failed this roughly one run in five. Asserting the
    ///         serialisation itself would need a recording git runner rather than the real one.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task PrepareAsync_ForConcurrentJobsOnOneRepository_PreparesEachOfThem()
    {
        // Four, which the default preparation throttle admits at once, so all of them are in preparation
        // together. Eight was tried and met the same contention for twice the runtime.
        const int JobCount = 4;
        var manager = this.CreateManager();

        var preparations = await Task.WhenAll(
            Enumerable.Range(0, JobCount).Select(_ => manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None)));

        try
        {
            Assert.All(preparations, preparation => Assert.Null(preparation.Failure));
            Assert.Equal(JobCount, preparations.Select(preparation => preparation.Workspace!.Lease.WorkspaceKey).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(JobCount, Directory.GetDirectories(this.WorkspacesRoot).Length);

            // Every checkout is complete and readable, which a preparation that ran while another was
            // fetching into the same mirror need not have produced.
            foreach (var preparation in preparations)
            {
                Assert.Equal(
                    "after\n",
                    await preparation.Workspace!.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Source, CancellationToken.None));
            }
        }
        finally
        {
            foreach (var preparation in preparations.Where(preparation => preparation.Workspace is not null))
            {
                await preparation.Workspace!.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_ChecksOutTheHeadRevisionOnly()
    {
        var manager = this.CreateManager();

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Failure);
        var workspaceRoot = Path.GetDirectoryName(result.Workspace!.Lease.HeadWorkspacePath)!;
        Assert.Equal(["source"], Directory.GetDirectories(workspaceRoot).Select(Path.GetFileName).ToArray());

        await result.Workspace.DisposeAsync();
    }

    [Fact]
    public async Task Workspace_ReadsTheTargetSideFromTheObjectStore()
    {
        var manager = this.CreateManager();
        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            // The base revision is not checked out anywhere, so every one of these answers comes from the
            // mirror's object store.
            Assert.Equal("before\n", await workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Target, CancellationToken.None));
            Assert.Equal("after\n", await workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Source, CancellationToken.None));

            // A file the pull request adds has no target-side content.
            Assert.Null(await workspace.ReadFileAsync("added.txt", RepositorySearchBranchSides.Target, CancellationToken.None));
            Assert.Equal("added\n", await workspace.ReadFileAsync("added.txt", RepositorySearchBranchSides.Source, CancellationToken.None));

            var targetTree = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Target, CancellationToken.None);
            Assert.Equal(["changed.txt", "kept.txt"], targetTree);

            var sourceTree = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Source, CancellationToken.None);
            Assert.Equal(["added.txt", "changed.txt", "kept.txt"], sourceTree);

            // The marker git writes at the root of a linked worktree is not repository content.
            Assert.DoesNotContain(".git", sourceTree);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    ///     A filename with leading and trailing spaces is valid in git, and the listing reports it as stored.
    ///     Every step between that listing and the read has to carry it unchanged, or the file reads as
    ///     absent on the side it exists on.
    /// </summary>
    [Theory]
    [InlineData(RepositorySearchBranchSides.Source)]
    [InlineData(RepositorySearchBranchSides.Target)]
    public async Task Workspace_ReadsAFileWhoseNameHasSurroundingWhitespace(string branchSide)
    {
        // The name is added before the base commit, so it is present on both sides. The two sides read
        // through different code: the source side from the checkout, the target side from the object store
        // with its own listing and read commands.
        const string padded = " padded name .txt";
        await File.WriteAllTextAsync(Path.Combine(this._originPath, padded), "padded\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "padded base");
        this._baseSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "changed.txt"), "after padded\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "padded head");
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");

        var result = await this.CreateManager().PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            var tree = await workspace.GetFileTreeAsync(branchSide, CancellationToken.None);
            Assert.Contains(padded, tree);
            Assert.Equal("padded\n", await workspace.ReadFileAsync(padded, branchSide, CancellationToken.None));
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    ///     A target-side read that fails is not the same answer as a file the base revision does not have.
    ///     Returning no content for both would report a file the review could not read as one the pull
    ///     request adds.
    /// </summary>
    [Fact]
    public async Task Workspace_ReadingAnUnreadableTargetFile_FailsRatherThanReportingItAbsent()
    {
        // A blobless mirror holds the trees and none of the file contents, so a target-side read has to go to
        // the server for them. Removing the remote takes that away and makes the read fail while the path is
        // still resolvable, which is the state this distinguishes from an absent file. Deleting an object
        // instead would depend on whether git had packed it.
        var blobless = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Blobless,
        };
        var result = await this.CreateManager(blobless).PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            await GitAsync(workspace.Lease.MirrorPath, "remote", "remove", "origin");

            // The path is not in the base revision, which is an answer and not a failure.
            Assert.Null(await workspace.ReadFileAsync("added.txt", RepositorySearchBranchSides.Target, CancellationToken.None));

            // changed.txt, and not one of the files the two revisions share: the head checkout downloads the
            // contents of the revision it materialises, so a file whose content is the same on both sides is
            // present locally afterwards. The base version of a changed file is the one nothing has fetched.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReadFileAsync(
                "changed.txt", RepositorySearchBranchSides.Target, CancellationToken.None));
            Assert.Contains("target-side content", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    ///     What a revision holds at the path of a symbolic link is the link: git stores it as a blob whose
    ///     content is the target text. Opening the path in the checkout instead reads whatever it points at,
    ///     which reports another file's contents as this path's content, and for a link out of the checkout
    ///     puts a file the repository does not contain into the review context and from there into a
    ///     published comment. The two sides also have to agree, or a file the pull request never touched
    ///     reads as changed.
    /// </summary>
    [Fact]
    public async Task Workspace_ReadsASymbolicLinkAsTheTargetTextTheRevisionStores()
    {
        var secretPath = Path.Combine(this._root, "outside-the-checkout.txt");
        await File.WriteAllTextAsync(secretPath, "host content\n");

        try
        {
            File.CreateSymbolicLink(Path.Combine(this._originPath, "alias.txt"), "kept.txt");
            File.CreateSymbolicLink(Path.Combine(this._originPath, "leak.txt"), secretPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a link is privileged on Windows, and this host does not grant it. The behaviour under
            // test is a property of the reader, not of the platform, so the case is skipped rather than
            // reported as a failure of the code.
            Skip.If(true, $"creating a symbolic link is not permitted here: {ex.Message}");
        }

        // The links are committed before the base commit, so both sides of the review hold them and the two
        // reads can be compared. The head commit that follows is what makes this a pull request.
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "add links");
        this._baseSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "changed.txt"), "after links\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "head over links");
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");

        var result = await this.CreateManager().PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            var tree = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Source, CancellationToken.None);
            Assert.Contains("alias.txt", tree);
            Assert.Contains("leak.txt", tree);

            // A link that stays inside the checkout: the file it points at is readable, so a read through it
            // would succeed and would report "kept\n" here, disagreeing with the target side.
            var aliasSource = await workspace.ReadFileAsync("alias.txt", RepositorySearchBranchSides.Source, CancellationToken.None);
            var aliasTarget = await workspace.ReadFileAsync("alias.txt", RepositorySearchBranchSides.Target, CancellationToken.None);
            Assert.Equal("kept.txt", aliasSource);
            Assert.Equal(aliasTarget, aliasSource);

            var leak = await workspace.ReadFileAsync("leak.txt", RepositorySearchBranchSides.Source, CancellationToken.None);
            Assert.DoesNotContain("host content", leak);
            Assert.Contains("outside-the-checkout.txt", leak);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    ///     A tree holds no entries below a symbolic link, so a path that leads through one names nothing in
    ///     the revision. The paths a review reads are the ones it asks for rather than the ones the listing
    ///     returned, so a link to a directory and a request for a path below it are enough to reach a file
    ///     outside the checkout without the pull request containing that path at all.
    /// </summary>
    [Fact]
    public async Task Workspace_RefusesAPathThatLeadsThroughADirectoryLinkAndLeavesItOutOfTheListing()
    {
        var outsideDirectory = Path.Combine(this._root, "outside-directory");
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllTextAsync(Path.Combine(outsideDirectory, "secret.txt"), "host content\n");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(this._originPath, "linked-directory"), outsideDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, $"creating a symbolic link is not permitted here: {ex.Message}");
        }

        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "add a link to a directory out of the tree");
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");

        var result = await this.CreateManager().PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            var throughTheLink = await workspace.ReadFileAsync(
                "linked-directory/secret.txt",
                RepositorySearchBranchSides.Source,
                CancellationToken.None);
            Assert.Null(throughTheLink);

            // Walking the checkout followed the link and listed what is under it as repository content.
            var tree = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Source, CancellationToken.None);
            Assert.DoesNotContain("linked-directory/secret.txt", tree);

            // The link itself is an entry of the revision, and its content is where it points.
            Assert.Contains("linked-directory", tree);
            var link = await workspace.ReadFileAsync("linked-directory", RepositorySearchBranchSides.Source, CancellationToken.None);
            Assert.Contains("outside-directory", link);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    ///     A submodule is a commit in another repository, which this one does not hold. Offering it as a file
    ///     and then failing to read it would abort the review of every repository that has one.
    /// </summary>
    [Fact]
    public async Task Workspace_LeavesSubmodulesOutOfTheTargetTreeAndReadsThemAsUnavailable()
    {
        var submodulePath = Path.Combine(this._root, "submodule-origin");
        Directory.CreateDirectory(submodulePath);
        await GitAsync(submodulePath, "init", "--initial-branch=main");
        await GitAsync(submodulePath, "config", "user.email", "tests@example.invalid");
        await GitAsync(submodulePath, "config", "user.name", "Tests");
        await GitAsync(submodulePath, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(submodulePath, "inner.txt"), "inner\n");
        await GitAsync(submodulePath, "add", "-A");
        await GitAsync(submodulePath, "commit", "-m", "inner");

        // The submodule is added before the base commit, so it is on both sides of the review.
        await GitAsync(this._originPath, "-c", "protocol.file.allow=always", "submodule", "add", "-q", submodulePath, "vendor/sub");
        await GitAsync(this._originPath, "commit", "-m", "add a submodule");
        this._baseSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "changed.txt"), "after submodule\n");
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", "head with a submodule");
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");

        var result = await this.CreateManager().PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(result.Failure);
        var workspace = result.Workspace!;

        try
        {
            var tree = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Target, CancellationToken.None);
            Assert.DoesNotContain("vendor/sub", tree);
            Assert.Contains(".gitmodules", tree);

            // Asked for anyway, it reports no content instead of failing the review.
            Assert.Null(await workspace.ReadFileAsync("vendor/sub", RepositorySearchBranchSides.Target, CancellationToken.None));
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task Workspace_GetFileTree_IsComputedOncePerSide()
    {
        var manager = this.CreateManager();
        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        var workspace = result.Workspace!;

        try
        {
            var first = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Source, CancellationToken.None);
            var second = await workspace.GetFileTreeAsync(RepositorySearchBranchSides.Source, CancellationToken.None);

            // Both revisions are fixed for the life of the lease, so the second call returns the cached
            // answer and does not walk the checkout again.
            Assert.Same(first, second);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task Workspace_ChangedFilesAndDiff_ComeFromTheHeadCheckout()
    {
        var manager = this.CreateManager();
        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        var workspace = result.Workspace!;

        try
        {
            var changed = await workspace.GetChangedFilesAsync(CancellationToken.None);
            Assert.Equal(
                [("added.txt", ChangeType.Add), ("changed.txt", ChangeType.Edit)],
                changed.Select(file => (file.Path, file.ChangeType)).OrderBy(file => file.Path).ToArray());

            var diff = await workspace.GetUnifiedDiffAsync("changed.txt", CancellationToken.None);
            Assert.Contains("-before", diff);
            Assert.Contains("+after", diff);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task PrepareAsync_RepacksAMirrorThatHasAccumulatedPackfiles()
    {
        var manager = this.CreateManager();
        var first = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(first.Failure);
        var mirrorPath = first.Workspace!.Lease.MirrorPath;
        await first.Workspace.DisposeAsync();

        // Git's automatic maintenance is disabled for this mirror, because the repack under test covers the
        // case where that maintenance cannot run. Each fetch is also configured to write a packfile instead
        // of loose objects, so the pack count grows.
        await GitAsync(mirrorPath, "config", "gc.auto", "0");
        await GitAsync(mirrorPath, "config", "fetch.unpackLimit", "1");
        await this.AccumulatePackfilesAsync(mirrorPath, 55);
        Assert.True(CountPackfiles(mirrorPath) > 50);

        var second = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(second.Failure);
        Assert.True(
            CountPackfiles(mirrorPath) < 10,
            $"expected the mirror to be repacked, found {CountPackfiles(mirrorPath)} packfiles");

        // The review still reads what it read before the repack.
        Assert.Equal("before\n", await second.Workspace!.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Target, CancellationToken.None));
        await second.Workspace.DisposeAsync();
    }

    [Fact]
    public async Task PrepareAsync_LeavesAMirrorAloneWhileAReviewIsReadingIt()
    {
        var manager = this.CreateManager();
        var holder = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(holder.Failure);
        var mirrorPath = holder.Workspace!.Lease.MirrorPath;

        await GitAsync(mirrorPath, "config", "gc.auto", "0");
        await GitAsync(mirrorPath, "config", "fetch.unpackLimit", "1");
        await this.AccumulatePackfilesAsync(mirrorPath, 55);
        var packsBefore = CountPackfiles(mirrorPath);

        // The lease from the first preparation is still held, so this preparation must not repack.
        var second = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(second.Failure);
        Assert.True(
            CountPackfiles(mirrorPath) >= packsBefore,
            "a mirror that is still leased must not be repacked");

        await second.Workspace!.DisposeAsync();
        await holder.Workspace.DisposeAsync();
    }

    [Fact]
    public async Task PrepareAsync_WithBloblessPolicy_FetchesTreesWithoutFileContents()
    {
        var options = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Blobless,
        };
        var manager = this.CreateManager(options);

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Failure);
        var mirrorPath = result.Workspace!.Lease.MirrorPath;
        Assert.Equal(
            "blob:none",
            await GitConfigValueAsync(mirrorPath, "remote.origin.partialclonefilter"));

        // Content the fetch left on the server is still readable: the read downloads what it needs.
        Assert.Equal("before\n", await result.Workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Target, CancellationToken.None));
        Assert.Equal("after\n", await result.Workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Source, CancellationToken.None));
        await result.Workspace.DisposeAsync();
    }

    [Fact]
    public async Task PrepareAsync_BackOnTheFullPolicy_StopsFilteringAnExistingMirror()
    {
        var blobless = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Blobless,
        };
        var first = await this.CreateManager(blobless)
            .PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(first.Failure);
        var mirrorPath = first.Workspace!.Lease.MirrorPath;
        await first.Workspace.DisposeAsync();

        // A mirror keeps the filter it was fetched under, so widening the policy has to say so.
        var second = await this.CreateManager()
            .PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(second.Failure);
        Assert.Equal(
            string.Empty,
            await GitConfigValueAsync(mirrorPath, "remote.origin.partialclonefilter"));

        // Clearing the filter does not by itself bring down what the filtered fetch omitted, so the file
        // contents have to be present and the promisor remote gone. While it is set, reads can still reach
        // the server for objects, which is what the full policy says they no longer do.
        Assert.Equal(string.Empty, await GitConfigValueAsync(mirrorPath, "remote.origin.promisor"));
        Assert.True(await CountBlobsAsync(mirrorPath) > 0, "the mirror still holds no file contents");

        await second.Workspace!.DisposeAsync();
    }

    [Fact]
    public async Task PrepareAsync_FromBloblessToShallow_StopsFilteringAndBringsContentsDown()
    {
        var blobless = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Blobless,
        };
        var first = await this.CreateManager(blobless)
            .PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Null(first.Failure);
        var mirrorPath = first.Workspace!.Lease.MirrorPath;
        await first.Workspace.DisposeAsync();

        // The shallow policy is narrower in history and wider in content, so it has to clear the filter as
        // the full policy does. Leaving it set would keep every read reaching the server for file contents.
        var shallow = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Shallow,
            FetchDepth = 50,
        };
        var second = await this.CreateManager(shallow)
            .PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(second.Failure);
        Assert.Equal(string.Empty, await GitConfigValueAsync(mirrorPath, "remote.origin.partialclonefilter"));
        Assert.Equal(string.Empty, await GitConfigValueAsync(mirrorPath, "remote.origin.promisor"));
        Assert.True(await CountBlobsAsync(mirrorPath) > 0, "the mirror still holds no file contents");

        await second.Workspace!.DisposeAsync();
    }

    [Fact]
    public async Task PrepareAsync_WithShallowPolicy_FetchesABoundedHistory()
    {
        var options = new ReviewWorkspaceOptions
        {
            RootPath = this._root,
            FetchDepthPolicy = ReviewWorkspaceFetchDepthPolicies.Shallow,
            FetchDepth = 1,
        };

        // Depth 1 keeps the head commit only, so the merge base of the two revisions is outside the fetched
        // history and cannot be resolved. This is the limitation of a bounded depth. Preparation reports a
        // failure; it does not fall back to a different base and produce a diff against it.
        var result = await this.CreateManager(options)
            .PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.NotNull(result.Failure);
        Assert.Equal("workspace_prepare_failed", result.Failure!.Code);
        Assert.True(File.Exists(Path.Combine(Directory.GetDirectories(this.MirrorsRoot).Single(), "shallow")));
    }

    private ReviewRepositoryWorkspaceRequest CreateRequest(Guid jobId)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "1", "acme", "acme/propr");
        return new ReviewRepositoryWorkspaceRequest(
            jobId,
            Guid.NewGuid(),
            ScmProvider.GitHub,
            "acme",
            repository,
            42,
            new ReviewRevision(this._headSha, this._baseSha, null, null, null),
            "feature/change",
            "main");
    }

    private GitReviewRepositoryWorkspaceManager CreateManager(ReviewWorkspaceOptions? options = null)
    {
        var resolved = Microsoft.Extensions.Options.Options.Create(options ?? new ReviewWorkspaceOptions { RootPath = this._root });
        var remoteResolver = Substitute.For<IReviewWorkspaceRemoteResolver>();
        remoteResolver.ResolveAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewWorkspaceRemoteRef(
                    ScmProvider.GitHub,
                    this._originPath,
                    ["+refs/heads/*:refs/remotes/origin/*"],
                    "acme/propr",
                    "credential-scope",
                    SupportsLocalFetch: true));

        return new GitReviewRepositoryWorkspaceManager(
            resolved,
            remoteResolver,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            new ReviewWorkspaceCleanupService(resolved, NullLogger<ReviewWorkspaceCleanupService>.Instance),
            new ReviewWorkspacePreparationThrottle(resolved),
            NullLogger<GitReviewRepositoryWorkspaceManager>.Instance);
    }

    /// <summary>Fetches one new commit at a time until the mirror holds more than <paramref name="count" /> packfiles.</summary>
    private async Task AccumulatePackfilesAsync(string mirrorPath, int count)
    {
        for (var index = 0; CountPackfiles(mirrorPath) <= count; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(this._originPath, $"filler-{index}.txt"), $"{index}\n");
            await GitAsync(this._originPath, "add", "-A");
            await GitAsync(this._originPath, "commit", "-m", $"filler {index}");
            await GitAsync(mirrorPath, "fetch", "origin", "+refs/heads/*:refs/remotes/origin/*");
        }
    }

    private static int CountPackfiles(string mirrorPath)
    {
        var packDirectory = Path.Combine(mirrorPath, "objects", "pack");
        return Directory.Exists(packDirectory) ? Directory.GetFiles(packDirectory, "*.pack").Length : 0;
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunAsync(workingDirectory, arguments);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    /// <summary>
    ///     Output of a command that has to succeed. Returning the output of a failed command would let an
    ///     unrelated git failure satisfy an assertion that expects an empty answer.
    /// </summary>
    private static async Task<string> GitOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunAsync(workingDirectory, arguments);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    /// <summary>How many blobs the mirror holds, which is zero while it is a partial clone.</summary>
    private static async Task<int> CountBlobsAsync(string mirrorPath)
    {
        var result = await RunAsync(mirrorPath, ["cat-file", "--batch-all-objects", "--batch-check", "--unordered"]);
        Assert.True(result.ExitCode == 0, $"listing objects failed: {result.StandardError}");
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(" blob ", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Value of a local configuration key, or an empty string when it is not set. Exit code 1 is git's
    ///     answer for a key that has no value, and is the expected result wherever a test asserts that a
    ///     setting was cleared.
    /// </summary>
    private static async Task<string> GitConfigValueAsync(string mirrorPath, string key)
    {
        var result = await RunAsync(mirrorPath, ["config", "--local", "--get", key]);
        Assert.True(
            result.ExitCode is 0 or 1,
            $"git config --get {key} failed with exit code {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        return new GitCommandRunner(NullLogger<GitCommandRunner>.Instance)
            .RunAsync(workingDirectory, arguments, null, CancellationToken.None);
    }

    private static void TryDeleteRoot(string root)
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
