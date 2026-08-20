// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class GitCommandRunner(ILogger<GitCommandRunner> logger)
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
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        bool preserveStandardOutput = false)
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

        // Each command below acts on the repository named by its working directory. An inherited override of
        // that is removed before the command runs.
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

        using var process = new Process { StartInfo = startInfo };

        logger.LogDebug(
            "Running git command in {WorkingDirectory}: git {Arguments}",
            workingDirectory,
            SanitizeForLog(string.Join(' ', arguments)));

        process.Start();
        process.StandardInput.Close();

        var outputTask = preserveStandardOutput
            ? process.StandardOutput.ReadToEndAsync(ct)
            : ReadLinesAsync(process.StandardOutput, ct);
        var errorTask = ReadLinesAsync(process.StandardError, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outputTask, errorTask);

        return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
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
