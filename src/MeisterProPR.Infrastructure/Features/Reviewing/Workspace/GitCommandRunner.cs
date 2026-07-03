// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Workspace;

internal sealed class GitCommandRunner(ILogger<GitCommandRunner> logger)
{
    // The runtime container and WSL/Linux dev hosts install git at this fixed path (see the gittools
    // stage in Dockerfile/procursor.dockerfile), so resolving it directly avoids an ambient PATH search.
    // Windows dev hosts (scripts/run-local.ps1) have no equivalent fixed path, so fall back to a PATH
    // search there.
    private static readonly string GitExecutablePath = OperatingSystem.IsWindows() ? "git" : "/usr/bin/git";

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct)
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
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        logger.LogDebug(
            "Running git command in {WorkingDirectory}: git {Arguments}",
            workingDirectory,
            SanitizeForLog(string.Join(' ', arguments)));

        process.Start();
        process.StandardInput.Close();

        var outputTask = ConsumeAsync(process.StandardOutput, standardOutput, ct);
        var errorTask = ConsumeAsync(process.StandardError, standardError, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outputTask, errorTask);

        return new GitCommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    // Git arguments include user-controlled values (remote URLs, refs, branch names from the
    // review request), so strip line breaks before logging to prevent forged log entries.
    private static string SanitizeForLog(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static async Task ConsumeAsync(StreamReader reader, StringBuilder buffer, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            buffer.AppendLine(line);
        }
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
