// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

[Collection("GitCommandLine")]
public sealed class GitCommandRunnerTests
{
    /// <summary>
    ///     Every command the workspace layer runs names its repository by working directory. These two
    ///     variables override that from the environment, so a command would report and act on a different
    ///     repository. Git hooks export <c>GIT_DIR</c> to every command they run, so a host process started
    ///     from a hook passes it on.
    /// </summary>
    /// <remarks>
    ///     Only the variables whose override this command actually observes are listed. The runner removes
    ///     more of them, and the object-store ones are covered by the test below; asserting the resolved git
    ///     directory for a variable that does not change it would pass whether or not the value was removed.
    /// </remarks>
    [Theory]
    [InlineData("GIT_DIR", "--absolute-git-dir")]
    [InlineData("GIT_COMMON_DIR", "--git-common-dir")]
    public async Task RunAsync_IgnoresAnInheritedRepositoryLocation(string variable, string revParseOption)
    {
        var root = CreateRoot();
        var repositoryPath = Path.Combine(root, "repository");
        var decoyPath = Path.Combine(root, "decoy");
        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(decoyPath);

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        try
        {
            (await runner.RunAsync(repositoryPath, ["init", "--bare"], null, CancellationToken.None))
                .EnsureSuccess("init repository");
            (await runner.RunAsync(decoyPath, ["init", "--bare"], null, CancellationToken.None))
                .EnsureSuccess("init decoy");

            // The variable has to be set on this process for the child to inherit it, which is the condition
            // under test. Its previous value is restored afterwards, and the decoy repository absorbs any
            // command that does honour the inherited value. The class is collected with the other tests that
            // start git, so none of them runs while this is set.
            var previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, decoyPath);
            try
            {
                // Each variable is asserted through the option that reports what it overrides:
                // --absolute-git-dir does not change with GIT_COMMON_DIR, so using it for both would pass
                // whether or not that one was removed.
                var result = await runner.RunAsync(
                    repositoryPath,
                    ["rev-parse", revParseOption],
                    null,
                    CancellationToken.None);

                result.EnsureSuccess("resolve git directory");
                Assert.Equal(
                    Path.GetFullPath(repositoryPath),
                    Path.GetFullPath(Path.Combine(repositoryPath, result.StandardOutput.Trim())));

                // Nothing was written to the decoy: it holds no commits and no configuration of its own.
                var decoyObjects = await runner.RunAsync(
                    decoyPath,
                    ["count-objects", "-v"],
                    null,
                    CancellationToken.None);
                decoyObjects.EnsureSuccess("count decoy objects");
                Assert.Contains("count: 0", decoyObjects.StandardOutput, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    ///     The object-directory variables redirect where objects are written and read, which the resolved git
    ///     directory does not show. Writing an object and finding it in the repository is what shows it.
    /// </summary>
    [Theory]
    [InlineData("GIT_OBJECT_DIRECTORY")]
    public async Task RunAsync_WritesObjectsIntoTheNamedRepository_WhateverTheEnvironmentPointsAt(string variable)
    {
        var root = CreateRoot();
        var repositoryPath = Path.Combine(root, "repository");
        var decoyPath = Path.Combine(root, "decoy");
        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(decoyPath);

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        try
        {
            (await runner.RunAsync(repositoryPath, ["init"], null, CancellationToken.None))
                .EnsureSuccess("init repository");
            (await runner.RunAsync(decoyPath, ["init", "--bare"], null, CancellationToken.None))
                .EnsureSuccess("init decoy");

            var previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, Path.Combine(decoyPath, "objects"));
            try
            {
                await File.WriteAllTextAsync(Path.Combine(repositoryPath, "file.txt"), "written\n");
                var hash = await runner.RunAsync(
                    repositoryPath,
                    ["hash-object", "-w", "file.txt"],
                    null,
                    CancellationToken.None);
                hash.EnsureSuccess("write object");
                var id = hash.StandardOutput.Trim();

                Assert.True(
                    File.Exists(Path.Combine(repositoryPath, ".git", "objects", id[..2], id[2..])),
                    "the object was not written into the repository the command named");
                Assert.False(
                    File.Exists(Path.Combine(decoyPath, "objects", id[..2], id[2..])),
                    "the object was written into the repository the environment pointed at");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    ///     An alternate object store is read-only, so writing an object says nothing about whether the
    ///     variable naming it was honoured. An object that exists only in the alternate store does: the
    ///     command can read it while the variable is in effect, and cannot once it has been removed.
    /// </summary>
    [Fact]
    public async Task RunAsync_DoesNotReadObjectsFromAnInheritedAlternateStore()
    {
        var root = CreateRoot();
        var repositoryPath = Path.Combine(root, "repository");
        var decoyPath = Path.Combine(root, "decoy");
        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(decoyPath);

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
        try
        {
            (await runner.RunAsync(repositoryPath, ["init"], null, CancellationToken.None))
                .EnsureSuccess("init repository");
            (await runner.RunAsync(decoyPath, ["init", "--bare"], null, CancellationToken.None))
                .EnsureSuccess("init decoy");

            await File.WriteAllTextAsync(Path.Combine(decoyPath, "only-here.txt"), "only in the decoy\n");
            var written = await runner.RunAsync(
                decoyPath,
                ["hash-object", "-w", "only-here.txt"],
                null,
                CancellationToken.None);
            written.EnsureSuccess("write object into the decoy");
            var objectId = written.StandardOutput.Trim();

            var previous = Environment.GetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES");
            Environment.SetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES", Path.Combine(decoyPath, "objects"));
            try
            {
                var read = await runner.RunAsync(
                    repositoryPath,
                    ["cat-file", "-e", objectId],
                    null,
                    CancellationToken.None);

                Assert.True(
                    read.ExitCode != 0,
                    "the object was read through the alternate store the environment named");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES", previous);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RunAsync_PreservingStandardOutput_ReturnsContentAsItWasWritten()
    {
        var root = CreateRoot();
        var repositoryPath = Path.Combine(root, "repository");
        Directory.CreateDirectory(repositoryPath);
        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        try
        {
            (await runner.RunAsync(repositoryPath, ["init"], null, CancellationToken.None))
                .EnsureSuccess("init repository");

            // No trailing newline, and one line ending that a line-by-line read would rewrite.
            const string content = "first\r\nsecond";
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "file.txt"), content);
            var hash = await runner.RunAsync(
                repositoryPath,
                ["hash-object", "-w", "file.txt"],
                null,
                CancellationToken.None);
            hash.EnsureSuccess("write object");

            var read = await runner.RunAsync(
                repositoryPath,
                ["cat-file", "blob", hash.StandardOutput.Trim()],
                null,
                CancellationToken.None,
                preserveStandardOutput: true);

            read.EnsureSuccess("read object");
            Assert.Equal(content, read.StandardOutput);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-command-runner-" + Guid.NewGuid().ToString("N"));
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
