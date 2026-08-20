// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Diagnostics;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Fetches the repository the review needs, from the control plane rather than from the source-control
///     provider.
///     <para>
///         The runner holds no source-control credential, so it cannot clone from the provider at all. The
///         control plane serves its own mirror over git's smart HTTP protocol, authorized per lease, which
///         is what lets a host review code it has no standing permission to read.
///     </para>
///     <para>
///         Only the head revision is checked out. A review compares two trees, and the tools that read the
///         repository read them as "source" and "target", but the target side is served from the mirror's
///         object store at the base commit — so what a file was is still readable without writing a second
///         complete copy of the repository to this host's disk.
///     </para>
/// </summary>
public sealed partial class WorkspaceFetcher(
    IOptions<RunnerHostOptions> options,
    ILogger<WorkspaceFetcher> logger)
{
    /// <summary>
    ///     Clones the manifest's workspace into a directory of this job's own and materializes both
    ///     revisions, returning the lease the pipeline's repository tools read through.
    ///     <para>
    ///         Job-scoped: the directory is named for the job and removed when the job ends, so nothing a
    ///         review read stays behind on a host that may be recycled or imaged.
    ///     </para>
    /// </summary>
    /// <param name="manifest">The manifest naming the fetch path and the commits to check out.</param>
    /// <param name="credential">The runner credential, presented so the control plane can authorize the fetch.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<ReviewRepositoryWorkspaceLease> FetchAsync(
        RunnerJobManifest manifest,
        string credential,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var workRoot = Path.Combine(options.Value.WorkRootPath, manifest.JobId.ToString("D"));
        Directory.CreateDirectory(workRoot);

        var mirror = Path.Combine(workRoot, "mirror");

        // The mirror is served from the granting replica's local disk, so the fetch has to go there when the
        // manifest names it. Sent through a load balancer, it can reach a replica that holds no workspace
        // for this job.
        var remote = RunnerReplicaAffinity.ResolveAbsolute(
            manifest.ServedBy,
            options.Value.ControlPlaneUrl,
            manifest.Workspace.FetchPath).ToString();

        LogFetching(logger, manifest.JobId, manifest.Workspace.HeadSha);

        // Initialised and fetched rather than cloned, because of how the control plane's mirror is
        // organised: every ref it holds lives under refs/remotes/, both the branches it tracks and the
        // per-review refs it fetches from the provider. `git clone` asks for refs/heads/*, matches none of
        // them, and produces an empty repository with no objects and no error, so the failure only appears
        // later as an unresolvable head commit. The explicit refspec takes the refs as they are.
        await RunGitAsync(workRoot, ct, "init", "--bare", mirror);
        await RunGitAsync(mirror, ct, "remote", "add", "origin", remote);

        // The credential is sent in a header rather than in the URL. A credential in a remote URL is written
        // into .git/config and every subsequent git error message, which is a durable copy of a secret on
        // a host that exists to be disposable.
        await RunGitAsync(
            mirror,
            ct,
            "-c",
            $"http.extraHeader=X-ProPR-Runner-Credential: {credential}",
            "fetch",
            "--no-tags",
            "origin",
            "+refs/*:refs/*");

        var head = Path.Combine(workRoot, "source");
        await RunGitAsync(mirror, ct, "worktree", "add", "--detach", "--force", head, manifest.Workspace.HeadSha);

        // What the two revisions actually diverged from. Diffing head against the base tip instead would
        // report every change the target branch collected since, as though this review had made them.
        var mergeBase = await ReadGitAsync(
            mirror,
            ct,
            "merge-base",
            manifest.Workspace.BaseSha,
            manifest.Workspace.HeadSha);

        LogFetched(logger, manifest.JobId, workRoot);

        var now = DateTimeOffset.UtcNow;
        return new ReviewRepositoryWorkspaceLease(
            manifest.JobId,
            manifest.JobId.ToString("D"),
            mirror,
            head,
            manifest.Workspace.HeadSha,
            manifest.Workspace.BaseSha,
            mergeBase,
            now,
            now,
            "Active");
    }

    /// <summary>
    ///     Removes everything this job wrote. Called when the job ends however it ended, and at startup for
    ///     anything a previous life left behind.
    /// </summary>
    /// <param name="jobId">The job whose directory to remove, or null for every job directory.</param>
    public void Purge(Guid? jobId = null)
    {
        var root = options.Value.WorkRootPath;
        if (!Directory.Exists(root))
        {
            return;
        }

        var targets = jobId is null
            ? Directory.GetDirectories(root)
            : [Path.Combine(root, jobId.Value.ToString("D"))];

        foreach (var target in targets.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
#pragma warning disable CA1031 // A directory that will not delete must not keep the runner from continuing.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPurgeFailed(logger, target, ex);
            }
        }
    }

    private static Task RunGitAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
    {
        return ReadGitAsync(workingDirectory, ct, arguments);
    }

    private static async Task<string> ReadGitAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
    {
        // Named from the first argument other than a -c override, so a failed fetch reports "fetch" rather
        // than the config flag that preceded it.
        var operation = Array.Find(arguments, argument => !argument.StartsWith('-') && !argument.Contains('=', StringComparison.Ordinal))
                        ?? arguments[0];

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Ambient git state is stripped for the same reason the control plane's transport strips it: an
        // inherited GIT_DIR points a child git at an unrelated repository, and this one runs commands that
        // write.
        foreach (var inherited in new[]
                 {
                     "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_OBJECT_DIRECTORY",
                     "GIT_COMMON_DIR", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_GLOBAL",
                 })
        {
            startInfo.Environment.Remove(inherited);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("git could not be started to fetch the workspace.");

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            // The credential is in the arguments, so the failure names the command rather than echoing it.
            throw new InvalidOperationException($"git {operation} failed with exit code {process.ExitCode}: {await errors}");
        }

        return (await output).Trim();
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Information, Message = "Fetching the workspace for job {JobId} at {HeadSha}")]
    private static partial void LogFetching(ILogger logger, Guid jobId, string headSha);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Information, Message = "Workspace for job {JobId} is ready at {Path}")]
    private static partial void LogFetched(ILogger logger, Guid jobId, string path);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Warning, Message = "Could not purge {Path}; it will be retried at the next startup")]
    private static partial void LogPurgeFailed(ILogger logger, string path, Exception ex);
}
