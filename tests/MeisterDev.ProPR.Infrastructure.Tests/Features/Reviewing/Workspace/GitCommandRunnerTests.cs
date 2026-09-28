// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    ///     Cancelling the measurement of a diff has to end the command, not only stop reading it. Disposing
    ///     the wrapper releases the pipes and leaves the child running, and a review that was cancelled would
    ///     keep a git process working and the workspace open behind it.
    /// </summary>
    /// <remarks>
    ///     The command is a shell alias, so git owns a process tree and not a single child, and the kill has
    ///     to cover the whole tree. It writes to standard output so the read loop keeps running and observes
    ///     the cancellation, and it appends to a file so the test can see whether it is still alive
    ///     afterwards. The alias ignores SIGPIPE, so closing the pipe alone would not end it.
    /// </remarks>
    [SkippableFact]
    public async Task CountStandardOutputBytesAsync_Cancelled_EndsTheCommandItWasReading()
    {
        // The alias below is a POSIX shell script, and git runs an alias through the platform's shell.
        Skip.IfNot(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "The command under test is written for a POSIX shell.");

        var root = CreateRoot();
        var repositoryPath = Path.Combine(root, "repository");
        Directory.CreateDirectory(repositoryPath);
        var ticksPath = Path.Combine(repositoryPath, "ticks");
        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        try
        {
            (await runner.RunAsync(repositoryPath, ["init"], null, CancellationToken.None))
                .EnsureSuccess("init repository");

            using var cts = new CancellationTokenSource();
            var measuring = runner.CountStandardOutputBytesAsync(
                repositoryPath,
                [
                    "-c",
                    $"alias.writeforever=!trap '' PIPE; while true; do echo tick; echo tick >> \"{ticksPath}\"; sleep 0.05; done",
                    "writeforever",
                ],
                null,
                cts.Token);

            var started = await WaitForAsync(() => File.Exists(ticksPath), TimeSpan.FromSeconds(20));
            Assert.True(started, "the command under test never started writing");

            await cts.CancelAsync();

            // Bounded: a cancellation that does not end the command is the regression under test, and an
            // unbounded await would hang here with the process and the workspace still alive.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring.WaitAsync(TimeSpan.FromSeconds(30)));

            var ended = await WaitForStableLengthAsync(ticksPath, TimeSpan.FromSeconds(10));
            Assert.True(ended, "the command went on writing after the cancellation, so it was still running");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    ///     A tree kill that could not end every member reports an <see cref="AggregateException" /> of the
    ///     failures it collected. It used to escape the cancellation handler that called the kill, so a
    ///     cancelled review surfaced as a failed one. The command itself is gone, so nothing is reported.
    /// </summary>
    [Fact]
    public async Task EndAsync_AKillThatEndedTheCommandAndReportedAFailedTree_IsNotReported()
    {
        using var process = StartABlockingGitCommand();
        var logged = new List<string>();

        await GitCommandRunner.EndAsync(
            process,
            () =>
            {
                process.Kill();
                process.WaitForExit();
                throw new AggregateException(new Win32Exception(1, "a member of the tree could not be ended"));
            },
            new CapturingLogger(logged));

        Assert.True(process.HasExited);
        Assert.Empty(logged);
    }

    /// <summary>
    ///     A kill the platform refused leaves the command running and the workspace it was started in held.
    ///     The caller is unwinding on cancellation and cannot end it, so what it left behind is reported.
    /// </summary>
    [Fact]
    public async Task EndAsync_AKillThatLeftTheCommandRunning_ReportsTheProcessStillHoldingItsWorkspace()
    {
        using var process = StartABlockingGitCommand();
        var logged = new List<string>();

        try
        {
            await GitCommandRunner.EndAsync(
                process,
                () => throw new AggregateException(new Win32Exception(1, "a member of the tree could not be ended")),
                new CapturingLogger(logged));

            Assert.False(process.HasExited);
            Assert.Contains(
                logged,
                line => line.Contains(process.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>Keeps every line that was written to it.</summary>
    private sealed class CapturingLogger(List<string> lines) : ILogger<GitCommandRunner>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
        }
    }

    /// <summary>A git command that blocks on standard input until it is ended.</summary>
    private static Process StartABlockingGitCommand()
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "git" : "/usr/bin/git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("hash-object");
        startInfo.ArgumentList.Add("--stdin");

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        return process;
    }

    /// <summary>
    ///     Waits for the file to stop growing, up to <paramref name="timeout" />. The command under test
    ///     appends a line every 50 ms while it runs, so a length that is unchanged across a window many times
    ///     that long is the process having ended. This tolerates a write that was in flight when the kill
    ///     landed, and reports a command that is still running instead of waiting on it.
    /// </summary>
    private static async Task<bool> WaitForStableLengthAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var previousLength = -1L;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var length = new FileInfo(path).Length;
            if (length == previousLength)
            {
                return true;
            }

            previousLength = length;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        return false;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
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
