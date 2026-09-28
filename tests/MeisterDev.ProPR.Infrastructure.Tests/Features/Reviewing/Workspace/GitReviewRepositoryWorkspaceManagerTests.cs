// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using System.Net;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Infrastructure.Egress;
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

    // The measurements name the revisions the lease was taken for, so they describe the same pair as the
    // diff the reviewer is sent. Reading them from the checkout's own HEAD would make them depend on what
    // the working directory happens to point at.
    [Fact]
    public async Task Workspace_MeasuresTheLeasedRevisionPair_WhenTheCheckoutPointsElsewhere()
    {
        var manager = this.CreateManager();
        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);
        var workspace = result.Workspace!;

        try
        {
            // The checkout is moved off the leased head, as a detached worktree can be.
            await GitAsync(workspace.Lease.HeadWorkspacePath, "checkout", "--detach", this._baseSha);

            var changed = await workspace.GetChangedFilesAsync(CancellationToken.None);
            Assert.Equal(
                [("added.txt", ChangeType.Add), ("changed.txt", ChangeType.Edit)],
                changed.Select(file => (file.Path, file.ChangeType)).OrderBy(file => file.Path).ToArray());

            // One line removed and one added in changed.txt, plus the one line of added.txt.
            Assert.Equal(3, await workspace.CountChangedLinesAsync(["changed.txt", "added.txt"], CancellationToken.None));
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
        // the server for objects, which the full policy says they no longer do.
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

    /// <param name="jobId">The job the workspace is prepared for.</param>
    /// <param name="maxRepositoryMegabytes">The client's repository-size bound, or null for an unbounded client.</param>
    private ReviewRepositoryWorkspaceRequest CreateRequest(Guid jobId, int? maxRepositoryMegabytes = null)
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
            "main",
            MaxRepositoryMegabytes: maxRepositoryMegabytes);
    }

    [Fact]
    public async Task PrepareAsync_RemoteResolvingToAPrivateAddress_FailsBeforeGitRuns()
    {
        var git = new RecordingGitCommandRunner();
        var resolver = new FixedHostResolver(IPAddress.Parse("10.4.1.7"));
        var manager = this.CreateManager(
            remoteUrl: "https://git.internal.example/acme/propr.git",
            hostResolver: resolver,
            gitCommandRunner: git);

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.Equal("blocked_egress_address", result.Failure.Code);
        Assert.False(result.Failure.Retryable);
        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, result.Failure.Message, StringComparison.Ordinal);

        // The remote's own host was the one classified, and git never ran.
        Assert.Equal(["git.internal.example"], resolver.RequestedHosts);
        Assert.Empty(git.Invocations);
    }

    /// <param name="samplingInterval">
    ///     How often the repository-size watch measures the directory git is writing into. A case that has to
    ///     see the watch stop a command shortens it, because the commands here run for a fraction of the
    ///     second the composition waits between two measurements.
    /// </param>
    private GitReviewRepositoryWorkspaceManager CreateManager(
        ReviewWorkspaceOptions? options = null,
        string? remoteUrl = null,
        IOutboundHostResolver? hostResolver = null,
        EgressUrlPolicy? egressUrlPolicy = null,
        GitCommandRunner? gitCommandRunner = null,
        TimeSpan? samplingInterval = null)
    {
        var resolved = Microsoft.Extensions.Options.Options.Create(options ?? new ReviewWorkspaceOptions { RootPath = this._root });
        var remoteResolver = Substitute.For<IReviewWorkspaceRemoteResolver>();
        remoteResolver.ResolveAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewWorkspaceRemoteRef(
                    ScmProvider.GitHub,
                    remoteUrl ?? this._originPath,
                    ["+refs/heads/*:refs/remotes/origin/*"],
                    "acme/propr",
                    "credential-scope",
                    SupportsLocalFetch: true));

        // The strict posture, so the remote of every other test passes a guard that is switched on.
        var guard = new OutboundHostGuard(
            egressUrlPolicy ?? EgressUrlPolicy.Locked,
            hostResolver ?? new UnreachableHostResolver());

        return new GitReviewRepositoryWorkspaceManager(
            resolved,
            remoteResolver,
            gitCommandRunner ?? new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            new ReviewWorkspaceCleanupService(resolved, NullLogger<ReviewWorkspaceCleanupService>.Instance),
            new ReviewWorkspacePreparationThrottle(resolved),
            guard,
            NullLogger<GitReviewRepositoryWorkspaceManager>.Instance)
        {
            SizeSamplingInterval = samplingInterval ?? TimeSpan.FromSeconds(1),
        };
    }

    // git resolves the remote itself when it connects, so the addresses the guard classified are put in front
    // of that resolution. Without them a name rebound between the check and the fetch reaches an address this
    // installation refuses.
    [Theory]
    [InlineData("https://git.example.com/acme/propr.git", "git.example.com:443:93.184.216.34")]
    [InlineData("https://git.example.com:8443/acme/propr.git", "git.example.com:8443:93.184.216.34")]
    public async Task PrepareAsync_PinsTheApprovedAddressOnTheFetch(string remoteUrl, string expectedEntry)
    {
        var git = new RecordingGitCommandRunner();
        var resolver = new FixedHostResolver(IPAddress.Parse("93.184.216.34"));
        var manager = this.CreateManager(
            remoteUrl: remoteUrl,
            hostResolver: resolver,
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        // The ordered prefix, so the configuration is shown to reach git as options of the fetch and not as
        // arguments somewhere after the subcommand, where git would read them as refspecs.
        var fetch = git.Invocations.Single(arguments => arguments.Contains("fetch"));
        Assert.Equal(["git.example.com"], resolver.RequestedHosts);
        Assert.Equal(
            ["-c", "http.followRedirects=false", "-c", $"http.curloptResolve={expectedEntry}", "fetch"],
            fetch.Take(5));
    }

    // A server answering with a redirect to another host would send git to an address nothing classified, so
    // every command that reaches the remote carries the option that refuses one.
    [Fact]
    public async Task PrepareAsync_CarriesTheRedirectRefusalOnEveryCommandThatReachesTheRemote()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "https://git.example.com/acme/propr.git",
            hostResolver: new FixedHostResolver(IPAddress.Parse("93.184.216.34")),
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        var fetch = git.Invocations.Single(arguments => arguments.Contains("fetch"));
        var checkout = git.Invocations.Single(arguments => arguments.Contains("add") && arguments.Contains("worktree"));

        Assert.Contains("http.followRedirects=false", fetch);
        Assert.Contains("http.followRedirects=false", checkout);
    }

    // What the option does when a server answers with a 3xx: git fails the command, and the preparation
    // reports that failure instead of fetching from wherever the redirect pointed.
    [Fact]
    public async Task PrepareAsync_WhenTheRemoteAnswersTheFetchWithARedirect_Fails()
    {
        var git = new RecordingGitCommandRunner(RefuseARedirectOnTheFetch);
        var manager = this.CreateManager(
            remoteUrl: "https://git.example.com/acme/propr.git",
            hostResolver: new FixedHostResolver(IPAddress.Parse("93.184.216.34")),
            gitCommandRunner: git);

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.Contains("unable to update url base from redirection", result.Failure.Message, StringComparison.Ordinal);

        // The fetch is where it stopped, so nothing reached the remote afterwards.
        Assert.DoesNotContain(git.Invocations, arguments => arguments.Contains("worktree") && arguments.Contains("add"));
    }

    /// <summary>
    ///     Answers a fetch the way git does when it refuses a redirect, and only where the command carries the
    ///     option that refuses one. A runner that failed without it would leave the option untested.
    /// </summary>
    /// <param name="arguments">The arguments of one invocation.</param>
    private static GitCommandResult? RefuseARedirectOnTheFetch(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("fetch") && arguments.Contains("http.followRedirects=false")
            ? new GitCommandResult(128, string.Empty, "fatal: unable to update url base from redirection")
            : null;
    }

    // The scheme is not an address, so it is decided before anything is resolved: the credential git sends
    // with every request would otherwise cross the internet in the clear.
    [Fact]
    public async Task PrepareAsync_APublicRemoteOverPlainHttp_FailsBeforeGitRuns()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "http://git.example.com/acme/propr.git",
            gitCommandRunner: git);

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.Equal("blocked_egress_address", result.Failure.Code);
        Assert.False(result.Failure.Retryable);
        Assert.Contains("https", result.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task PrepareAsync_PinsEveryAddressTheHostResolvesTo()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "https://git.example.com/acme/propr.git",
            hostResolver: new FixedHostResolver(
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")),
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        var fetch = git.Invocations.Single(arguments => arguments.Contains("fetch"));
        Assert.Contains(
            "http.curloptResolve=git.example.com:443:93.184.216.34,[2606:2800:220:1:248:1893:25c8:1946]",
            fetch);
    }

    // The checkout of a partial clone downloads file contents from the same remote, so it connects to the
    // same approved addresses.
    [Fact]
    public async Task PrepareAsync_PinsTheApprovedAddressOnTheCheckout()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "https://git.example.com/acme/propr.git",
            hostResolver: new FixedHostResolver(IPAddress.Parse("93.184.216.34")),
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        // The ordered prefix, so the configuration is shown to reach git as options of the checkout and not
        // as arguments after the subcommand, where git would read them as paths.
        var checkout = git.Invocations.Single(arguments => arguments.Contains("add") && arguments.Contains("worktree"));
        Assert.Equal(
            ["-c", "http.followRedirects=false", "-c", "http.curloptResolve=git.example.com:443:93.184.216.34", "worktree"],
            checkout.Take(5));
    }

    // The opt-in leaves no address to pin, and a redirect off the remote still reaches a host nothing
    // classified, so the refusal stays on the command line.
    [Fact]
    public async Task PrepareAsync_WithTheOptIn_PinsNothingAndStillRefusesARedirect()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "https://git.internal.example/acme/propr.git",
            egressUrlPolicy: new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false),
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        var fetch = git.Invocations.Single(arguments => arguments.Contains("fetch"));
        Assert.Contains("http.followRedirects=false", fetch);
        Assert.DoesNotContain(
            git.Invocations.SelectMany(arguments => arguments),
            argument => argument.StartsWith("http.curloptResolve", StringComparison.Ordinal));
    }

    // A literal address is what git connects to; there is no second resolution to put anything in front of.
    // A redirect off it still leaves that address, so the refusal stays.
    [Fact]
    public async Task PrepareAsync_ARemoteOnALiteralAddress_PinsNothingAndStillRefusesARedirect()
    {
        var git = new RecordingGitCommandRunner();
        var manager = this.CreateManager(
            remoteUrl: "https://93.184.216.34/acme/propr.git",
            hostResolver: new FixedHostResolver(IPAddress.Parse("93.184.216.34")),
            gitCommandRunner: git);

        await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        var fetch = git.Invocations.Single(arguments => arguments.Contains("fetch"));
        Assert.Contains("http.followRedirects=false", fetch);
        Assert.DoesNotContain(
            git.Invocations.SelectMany(arguments => arguments),
            argument => argument.StartsWith("http.curloptResolve", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Records what each git command was given and answers as a command that succeeded with no output, so
    ///     the arguments a preparation builds are read without a repository or a server behind them.
    /// </summary>
    /// <param name="answer">
    ///     What one invocation answers, where a case needs a command to fail. Invocations it answers
    ///     <see langword="null" /> for succeed with no output.
    /// </param>
    private sealed class RecordingGitCommandRunner(Func<IReadOnlyList<string>, GitCommandResult?>? answer = null)
        : GitCommandRunner(NullLogger<GitCommandRunner>.Instance)
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public override Task<GitCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken ct,
            bool preserveStandardOutput = false)
        {
            this.Invocations.Add([.. arguments]);

            return Task.FromResult(answer?.Invoke(arguments) ?? new GitCommandResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>
    ///     Records what each git command was given and runs it, so a case can assert which commands a
    ///     preparation reached while git still does the work the case reads afterwards.
    /// </summary>
    private sealed class TrackingGitCommandRunner() : GitCommandRunner(NullLogger<GitCommandRunner>.Instance)
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public override Task<GitCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken ct,
            bool preserveStandardOutput = false)
        {
            this.Invocations.Add([.. arguments]);

            return base.RunAsync(workingDirectory, arguments, environment, ct, preserveStandardOutput);
        }
    }

    /// <summary>A resolver that fails the test if a remote these cases use ever needs a host resolved.</summary>
    private sealed class UnreachableHostResolver : IOutboundHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            throw new InvalidOperationException($"A local remote must not be resolved, and '{host}' was.");
        }
    }

    /// <summary>
    ///     A resolver answering with fixed addresses, standing in for a name server, and recording what it was
    ///     asked for so a test can tell the remote's host from any other.
    /// </summary>
    private sealed class FixedHostResolver(params IPAddress[] addresses) : IOutboundHostResolver
    {
        public List<string> RequestedHosts { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            this.RequestedHosts.Add(host);

            return Task.FromResult(addresses);
        }
    }

    /// <summary>
    ///     The repository-size bound is enforced where the bytes arrive: the fetch is stopped partway and what
    ///     it had written is removed, so a repository this client will not review leaves nothing in the cache.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_WhenTheMirrorPassesTheRepositorySizeLimit_StopsTheFetchAndRemovesTheMirror()
    {
        await this.AddIncompressibleCommitAsync(24);
        var manager = this.CreateManager(samplingInterval: TimeSpan.FromMilliseconds(5));

        var result = await manager.PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 1),
            CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.False(result.Failure.Retryable);

        var breach = result.Failure.RepositorySizeBreach;
        Assert.NotNull(breach);
        Assert.Equal(1, breach.LimitMegabytes);
        Assert.True(
            breach.MeasuredMegabytes > breach.LimitMegabytes,
            $"the measured size was {breach.MeasuredMegabytes} MB and the limit {breach.LimitMegabytes} MB");

        Assert.Empty(Directory.GetDirectories(this.MirrorsRoot));
    }

    /// <summary>
    ///     A transfer that starts and ends between two samples is never sampled. The bound holds for it
    ///     because the directory is measured again once the command has returned.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_WhenTheFetchEndsBeforeTheFirstSample_RefusesTheRepository()
    {
        await this.AddIncompressibleCommitAsync(4);

        // The sampling interval outlasts the whole preparation, so no sample can fire and the measurement
        // taken after the fetch returned is the one that has to refuse the repository.
        var manager = this.CreateManager(samplingInterval: TimeSpan.FromMinutes(5));

        var result = await manager.PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 1),
            CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.False(result.Failure.Retryable);

        var breach = result.Failure.RepositorySizeBreach;
        Assert.NotNull(breach);
        Assert.Equal(1, breach.LimitMegabytes);
        Assert.True(
            breach.MeasuredMegabytes > breach.LimitMegabytes,
            $"the measured size was {breach.MeasuredMegabytes} MB and the limit {breach.LimitMegabytes} MB");
    }

    /// <summary>
    ///     A mirror fetched for a client with a higher bound stays on disk. A client whose bound the mirror
    ///     passes is refused from the size the mirror has now, without transferring anything.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_WhenTheMirrorOnDiskAlreadyPassesTheLimit_RefusesBeforeTheFetch()
    {
        await this.AddIncompressibleCommitAsync(4);
        var git = new TrackingGitCommandRunner();

        var prepared = await this.CreateManager(gitCommandRunner: git).PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 64),
            CancellationToken.None);
        Assert.Null(prepared.Failure);
        Assert.NotNull(prepared.Workspace);
        await prepared.Workspace.DisposeAsync();

        git.Invocations.Clear();
        var result = await this.CreateManager(gitCommandRunner: git).PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 1),
            CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);
        Assert.False(result.Failure.Retryable);

        var breach = result.Failure.RepositorySizeBreach;
        Assert.NotNull(breach);
        Assert.Equal(1, breach.LimitMegabytes);
        Assert.True(
            breach.MeasuredMegabytes > breach.LimitMegabytes,
            $"the measured size was {breach.MeasuredMegabytes} MB and the limit {breach.LimitMegabytes} MB");

        // No transfer ran, and the mirror is left for the clients whose bound it fits.
        Assert.DoesNotContain(git.Invocations, arguments => arguments.Contains("fetch"));
        Assert.NotEmpty(Directory.GetDirectories(this.MirrorsRoot));
    }

    [Fact]
    public async Task PrepareAsync_WithARepositoryUnderTheLimit_PreparesTheWorkspace()
    {
        var manager = this.CreateManager(samplingInterval: TimeSpan.FromMilliseconds(5));

        var result = await manager.PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 64),
            CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Workspace);
        Assert.Equal(
            "after\n",
            await result.Workspace.ReadFileAsync("changed.txt", RepositorySearchBranchSides.Source, CancellationToken.None));

        await result.Workspace.DisposeAsync();
    }

    /// <summary>
    ///     The checkout is the second point at which a repository's size reaches this host, and a mirror that
    ///     passed the bound can still write a working copy that does not.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_WhenTheCheckoutPassesTheRepositorySizeLimit_StopsItAndRemovesTheWorktree()
    {
        // Text that repeats, so the fetched objects stay far under the bound and the working copy git writes
        // from them passes it. The checkout is then the step that has to be stopped.
        await this.AddCompressibleCommitAsync(48);
        var manager = this.CreateManager(samplingInterval: TimeSpan.FromMilliseconds(5));

        var result = await manager.PrepareAsync(
            this.CreateRequest(Guid.NewGuid(), maxRepositoryMegabytes: 8),
            CancellationToken.None);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Failure);

        var breach = result.Failure.RepositorySizeBreach;
        Assert.NotNull(breach);
        Assert.Equal(8, breach.LimitMegabytes);
        Assert.True(
            breach.MeasuredMegabytes > breach.LimitMegabytes,
            $"the measured size was {breach.MeasuredMegabytes} MB and the limit {breach.LimitMegabytes} MB");

        // The checkout is gone, and the mirror stays: it was fetched under the bound and is the cache the
        // next review of this repository reuses.
        Assert.Empty(Directory.GetDirectories(this.WorkspacesRoot));
        Assert.NotEmpty(Directory.GetDirectories(this.MirrorsRoot));
    }

    // A client that set no bound has every command run without a watch on it.
    [Fact]
    public async Task PrepareAsync_WithoutARepositorySizeLimit_PreparesARepositoryOfAnySize()
    {
        await this.AddIncompressibleCommitAsync(8);
        var manager = this.CreateManager(samplingInterval: TimeSpan.FromMilliseconds(5));

        var result = await manager.PrepareAsync(this.CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Workspace);
        await result.Workspace.DisposeAsync();
    }

    /// <summary>
    ///     Commits data that does not compress, so the objects the fetch transfers are about as large as the
    ///     file and the mirror grows past a small bound.
    /// </summary>
    /// <param name="megabytes">How much data to commit.</param>
    private async Task AddIncompressibleCommitAsync(int megabytes)
    {
        var content = new byte[megabytes * 1024 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content);
        await File.WriteAllBytesAsync(Path.Combine(this._originPath, "bulk.bin"), content);
        await this.CommitOriginAsync("bulk");
    }

    /// <summary>
    ///     Commits text that repeats, so the objects the fetch transfers are a fraction of the file and only
    ///     the working copy the checkout writes passes a small bound.
    /// </summary>
    /// <param name="megabytes">How much text to commit.</param>
    private async Task AddCompressibleCommitAsync(int megabytes)
    {
        var line = new string('a', 1023) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, megabytes * 1024));
        await File.WriteAllTextAsync(Path.Combine(this._originPath, "bulk.txt"), content);
        await this.CommitOriginAsync("bulk");
    }

    /// <summary>Commits what is in the origin's working tree and moves the reviewed head onto it.</summary>
    /// <param name="message">The commit message.</param>
    private async Task CommitOriginAsync(string message)
    {
        await GitAsync(this._originPath, "add", "-A");
        await GitAsync(this._originPath, "commit", "-m", message);
        this._headSha = await GitOutputAsync(this._originPath, "rev-parse", "HEAD");
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
