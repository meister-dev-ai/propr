// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The dispatch preparer parses <c>git diff --name-only</c> into the carry-forward set with
///     <c>core.quotePath</c> disabled. With quoting on, git answers <c>"src/caf\303\251.cs"</c> including the
///     surrounding quotes, which matches no stored file path, so a file that did change is carried forward
///     with the previous iteration's comments instead of being reviewed.
/// </summary>
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

    private readonly string _repoPath = Path.Combine(
        Path.GetTempPath(),
        $"propr-delta-quoting-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._repoPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is left for the operating system to clean. Failing the test run
            // over it would serve no purpose.
        }
    }

    [Fact]
    public async Task ANonAsciiPathInTheDelta_ComesBackUnquoted()
    {
        var git = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        Directory.CreateDirectory(this._repoPath);
        await this.RunGitAsync(git, "init", "-q");
        await this.RunGitAsync(git, "config", "user.email", "test@test.invalid");
        await this.RunGitAsync(git, "config", "user.name", "Test");

        Directory.CreateDirectory(Path.Combine(this._repoPath, "src"));
        var filePath = Path.Combine(this._repoPath, "src", "café.cs");
        await File.WriteAllTextAsync(filePath, "class A;");
        await this.RunGitAsync(git, "add", ".");
        await this.RunGitAsync(git, "commit", "-q", "-m", "baseline");
        var baseline = (await this.RunGitAsync(git, "rev-parse", "HEAD")).StandardOutput.Trim();

        await File.WriteAllTextAsync(filePath, "class A { }");
        await this.RunGitAsync(git, "add", ".");
        await this.RunGitAsync(git, "commit", "-q", "-m", "change");
        var current = (await this.RunGitAsync(git, "rev-parse", "HEAD")).StandardOutput.Trim();

        // The exact invocation the preparer's delta uses.
        var diff = await git.RunAsync(
            this._repoPath,
            ["-c", "core.quotePath=false", "diff", "--name-only", baseline, current],
            IsolatedGitEnvironment,
            CancellationToken.None);

        Assert.Equal(0, diff.ExitCode);
        Assert.Equal("src/café.cs", diff.StandardOutput.Trim());
    }

    private async Task<GitCommandResult> RunGitAsync(GitCommandRunner git, params string[] args)
    {
        var result = await git.RunAsync(this._repoPath, args, IsolatedGitEnvironment, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return result;
    }
}
