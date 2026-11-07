// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Carry-forward compares the head of the baseline with the reviewed head in the mirror and matches the
///     changed paths against the stored file results. The comparison returns every path as stored. With quoting,
///     git answers <c>"src/caf\303\251.cs"</c> including the surrounding quotes, which matches no stored file path,
///     so a file that did change would be carried forward with the previous iteration's comments instead of being
///     reviewed.
/// </summary>
[Collection("GitCommandLine")]
public sealed class MirrorDeltaQuotingTests : IDisposable
{
    // Git hooks export GIT_DIR and GIT_INDEX_FILE, and a child git process inherits them. Without
    // removing these, running the suite from a pre-commit hook commits this fixture's files into the
    // real repository instead of the temp one.
    private static readonly IReadOnlyDictionary<string, string?> IsolatedGitEnvironment =
        new Dictionary<string, string?>
        {
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_PREFIX"] = null,
            ["GIT_OBJECT_DIRECTORY"] = null,
            ["GIT_COMMON_DIR"] = null,
        };

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"propr-delta-quoting-{Guid.NewGuid():N}");

    private string RepoPath => Path.Combine(this._rootPath, "repo");

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._rootPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is left for the operating system to clean. Failing the test run
            // over it would serve no purpose.
        }
    }

    [Fact]
    public async Task ThePathsChangedSinceABaseline_ComeBackAsStored()
    {
        var git = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        await this.InitRepositoryAsync(git);

        Directory.CreateDirectory(Path.Combine(this.RepoPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "src", "café.cs"), "class A;");
        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "src", "with space.cs"), "class B;");
        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "src", "same.cs"), "class C;");
        var baseline = await this.CommitAsync(git, "baseline");

        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "src", "café.cs"), "class A { }");
        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "src", "with space.cs"), "class B { }");
        var current = await this.CommitAsync(git, "change");

        var changed = await this.CreateWorkspace(current).GetPathsChangedSinceAsync(baseline, CancellationToken.None);

        Assert.NotNull(changed);
        Assert.Equal(["src/café.cs", "src/with space.cs"], changed!.Order(StringComparer.Ordinal));
    }

    // Without a comparison, the caller cannot show any file to be unchanged, and carries nothing forward.
    [Fact]
    public async Task ARevisionTheMirrorDoesNotHold_IsAnsweredWithNull()
    {
        var git = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        await this.InitRepositoryAsync(git);
        await File.WriteAllTextAsync(Path.Combine(this.RepoPath, "file.cs"), "class A;");
        var current = await this.CommitAsync(git, "only");

        var changed = await this.CreateWorkspace(current)
            .GetPathsChangedSinceAsync("0123456789abcdef0123456789abcdef01234567", CancellationToken.None);

        Assert.Null(changed);
    }

    private GitReviewRepositoryWorkspace CreateWorkspace(string headSha)
    {
        var lease = new ReviewRepositoryWorkspaceLease(
            Guid.NewGuid(),
            "delta",
            this.RepoPath,
            this.RepoPath,
            headSha,
            headSha,
            headSha,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");
        var cleanupService = new ReviewWorkspaceCleanupService(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { RootPath = this._rootPath }),
            NullLogger<ReviewWorkspaceCleanupService>.Instance);
        return new GitReviewRepositoryWorkspace(
            lease,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger.Instance,
            cleanupService);
    }

    private async Task InitRepositoryAsync(GitCommandRunner git)
    {
        Directory.CreateDirectory(this.RepoPath);
        await this.RunGitAsync(git, "init", "-q");
        await this.RunGitAsync(git, "config", "user.email", "test@test.invalid");
        await this.RunGitAsync(git, "config", "user.name", "Test");
        await this.RunGitAsync(git, "config", "commit.gpgsign", "false");
    }

    private async Task<string> CommitAsync(GitCommandRunner git, string message)
    {
        await this.RunGitAsync(git, "add", ".");
        await this.RunGitAsync(git, "commit", "-q", "-m", message);
        return (await this.RunGitAsync(git, "rev-parse", "HEAD")).StandardOutput.Trim();
    }

    private async Task<GitCommandResult> RunGitAsync(GitCommandRunner git, params string[] args)
    {
        var result = await git.RunAsync(this.RepoPath, args, IsolatedGitEnvironment, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return result;
    }
}
