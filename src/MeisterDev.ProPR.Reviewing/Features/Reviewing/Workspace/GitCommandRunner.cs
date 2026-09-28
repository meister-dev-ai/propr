// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

/// <summary>Runs one git command and reports what it wrote and the code it exited with.</summary>
/// <remarks>
///     <see cref="RunAsync" /> is overridable so a caller's command line can be recorded without a git
///     process running, which is how the arguments a preparation builds are asserted.
/// </remarks>
/// <param name="logger">Where the command line is recorded at debug level.</param>
internal class GitCommandRunner(ILogger<GitCommandRunner> logger)
{
    // The runtime container and WSL/Linux dev hosts install git at this fixed path (see the gittools
    // stage in Dockerfile/procursor.dockerfile), so resolving it directly avoids an ambient PATH search.
    // Windows dev hosts (scripts/run-local.ps1) have no equivalent fixed path, so fall back to a PATH
    // search there.
    private static readonly string GitExecutablePath = OperatingSystem.IsWindows() ? "git" : "/usr/bin/git";

    /// <summary>
    ///     Environment variables that tell git which repository to act on, whatever its working directory is.
    /// </summary>
    /// <remarks>
    ///     Every command here names its repository by working directory. An inherited value for any of these
    ///     overrides that without reporting an error: a mirror fetch would fetch into the inherited
    ///     repository, and a commit-ish would be resolved against it. Git hooks export <c>GIT_DIR</c> and
    ///     <c>GIT_INDEX_FILE</c> to every command they run, so a host process started from a hook passes them
    ///     on.
    /// </remarks>
    private static readonly string[] RepositoryLocatingVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_PREFIX",
        "GIT_CEILING_DIRECTORIES",
    ];

    /// <param name="workingDirectory">Directory the command runs in.</param>
    /// <param name="arguments">Arguments passed to git, one per element.</param>
    /// <param name="environment">Extra environment entries; a null value removes the variable.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="preserveStandardOutput">
    ///     Read standard output exactly as written instead of line by line. Line-by-line reading rewrites line
    ///     endings and appends a final newline, which is harmless for the output of a command that is parsed
    ///     as lines, and wrong for a command whose output is file content.
    /// </param>
    public virtual async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        bool preserveStandardOutput = false)
    {
        var startInfo = BuildStartInfo(workingDirectory, arguments, environment);

        using var process = new Process { StartInfo = startInfo };

        logger.LogDebug(
            "Running git command in {WorkingDirectory}: git {Arguments}",
            workingDirectory,
            SanitizeForLog(string.Join(' ', arguments)));

        process.Start();
        process.StandardInput.Close();

        try
        {
            var outputTask = preserveStandardOutput
                ? process.StandardOutput.ReadToEndAsync(ct)
                : ReadLinesAsync(process.StandardOutput, ct);
            var errorTask = ReadLinesAsync(process.StandardError, ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(outputTask, errorTask);

            return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            await this.TerminateAsync(process);
            throw;
        }
    }

    /// <summary>
    ///     Runs git and reports how many bytes it wrote to standard output, keeping none of them. A diff whose
    ///     size is the question is exactly the output that must not be loaded to answer it: the bound exists to
    ///     stop the process taking on that much text.
    /// </summary>
    /// <param name="workingDirectory">Directory the command runs in.</param>
    /// <param name="arguments">Arguments passed to git, one per element.</param>
    /// <param name="environment">Extra environment entries; a null value removes the variable.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<GitCommandByteCount> CountStandardOutputBytesAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct)
    {
        var startInfo = BuildStartInfo(workingDirectory, arguments, environment);

        using var process = new Process { StartInfo = startInfo };

        logger.LogDebug(
            "Measuring git command output in {WorkingDirectory}: git {Arguments}",
            workingDirectory,
            SanitizeForLog(string.Join(' ', arguments)));

        process.Start();
        process.StandardInput.Close();

        try
        {
            var errorTask = ReadLinesAsync(process.StandardError, ct);
            var bytes = await CountBytesAsync(process.StandardOutput.BaseStream, ct);
            await process.WaitForExitAsync(ct);
            await errorTask;

            return new GitCommandByteCount(process.ExitCode, bytes, await errorTask);
        }
        catch (OperationCanceledException)
        {
            await this.TerminateAsync(process);
            throw;
        }
    }

    /// <summary>
    ///     Ends the command a cancelled call walked away from. Disposing the wrapper releases the handles and
    ///     leaves the child running, so a cancelled measurement of a large diff would keep git working and
    ///     holding the workspace open. Git delegates to child processes of its own, so the whole tree is
    ///     killed, and the exit is awaited so the workspace is free once this returns.
    /// </summary>
    private Task TerminateAsync(Process process)
    {
        return EndAsync(process, () => process.Kill(entireProcessTree: true), logger);
    }

    /// <summary>Ends <paramref name="process" /> and waits for it to be gone.</summary>
    /// <param name="process">The command's process wrapper.</param>
    /// <param name="killTree">Ends the process and the processes it started.</param>
    /// <param name="logger">Where a command that could not be ended is reported.</param>
    /// <remarks>
    ///     The kill is a parameter so a failed one can be exercised: the platform decides when a tree kill
    ///     fails, and the failure is what this handling exists for.
    /// </remarks>
    internal static async Task EndAsync(Process process, Action killTree, ILogger logger)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            killTree();
        }
        catch (InvalidOperationException)
        {
            // The process ended between the check and the kill, so there is nothing left to end.
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException or AggregateException)
        {
            // The platform refused the kill, or one member of the tree could not be ended, which
            // Kill(entireProcessTree: true) reports as an AggregateException of the failures it collected.
            // Either way the exception escapes the cancellation handler unless it is caught here.
            if (HasEnded(process))
            {
                // The command itself ended, so its workspace is free. What the tree kill could not reach is a
                // descendant git left behind, and there is no handle here to report it by.
                return;
            }

            // The command is still running and still holding the workspace it was started in. The caller is
            // unwinding on cancellation and cannot end it, so what it left behind is reported: the workspace
            // cannot be reused while that process has it open.
            logger.LogWarning(
                ex,
                "Failed to end the cancelled git command; process {ProcessId} is still running and still holds its workspace.",
                process.Id);
            return;
        }

        try
        {
            // Not the caller's token: it is already cancelled, and waiting on it would return before the
            // process is actually gone.
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // No process is associated with the wrapper any more, so the exit has already happened.
        }
    }

    /// <summary>Whether the command has ended.</summary>
    /// <param name="process">The command's process wrapper.</param>
    /// <remarks>
    ///     A wrapper holding no process has nothing left running, so it answers the same way an exited one
    ///     does. Asked while a failed kill is being handled, where an exception would leave the caller's
    ///     cancellation unwinding through this method.
    /// </remarks>
    private static bool HasEnded(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<long> CountBytesAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
        }

        return total;
    }

    private static ProcessStartInfo BuildStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("At least one git argument is required.", nameof(arguments));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Prevent git from hanging on a credential prompt when auth fails.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ASKPASS"] = "/bin/true";

        // Each command acts on the repository named by its working directory. An inherited override of that is
        // removed before the command runs.
        foreach (var variable in RepositoryLocatingVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                if (entry.Value is null)
                {
                    startInfo.Environment.Remove(entry.Key);
                }
                else
                {
                    startInfo.Environment[entry.Key] = entry.Value;
                }
            }
        }

        return startInfo;
    }

    // Git arguments include user-controlled values (remote URLs, refs, branch names from the
    // review request), so strip line breaks before logging to prevent forged log entries.
    private static string SanitizeForLog(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            buffer.AppendLine(line);
        }

        return buffer.ToString();
    }
}

/// <summary>The exit code, the number of bytes written to standard output, and standard error.</summary>
internal sealed record GitCommandByteCount(int ExitCode, long StandardOutputBytes, string StandardError);

internal sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string operation, string? sanitizedCommand = null)
    {
        if (this.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(this.StandardError)
            ? this.StandardOutput.Trim()
            : this.StandardError.Trim();
        throw new InvalidOperationException(
            $"Git {operation} failed with exit code {this.ExitCode}." +
            (string.IsNullOrWhiteSpace(sanitizedCommand) ? string.Empty : $" Command: {sanitizedCommand}.") +
            (string.IsNullOrWhiteSpace(message) ? string.Empty : $" Error: {message}"));
    }
}
