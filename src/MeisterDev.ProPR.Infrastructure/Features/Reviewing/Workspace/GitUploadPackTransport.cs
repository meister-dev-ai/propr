// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Diagnostics;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

/// <summary>
///     Serves a bare mirror over git's smart HTTP protocol by running <c>git upload-pack</c> and piping it.
///     <para>
///         Streamed rather than buffered, unlike every other git call this codebase makes. A pack for a
///         real repository is hundreds of megabytes, and holding one in memory per concurrent runner is the
///         opposite of the isolation this whole feature exists to buy.
///     </para>
///     <para>
///         Nothing here decides who may fetch, and the guard chain that does is worth naming because this
///         type is where an unauthorized fetch would surface. The endpoints sit behind the runner credential
///         scheme, so an unauthenticated request never reaches an action. The action resolves the caller from
///         the authenticated principal rather than the request, and <c>RunnerWorkspaceServer</c> authorizes
///         that caller against the job's live lease and generation, the same check every proxied call makes.
///         Only then is a mirror looked up, and it is looked up by job id in a registry the control plane
///         populated: a runner names a job it holds, never a path, so there is no directory for a caller to
///         traverse out of. The transfer ceiling is measured before a byte is sent.
///     </para>
/// </summary>
public sealed class GitUploadPackTransport : IGitUploadPackTransport
{
    /// <inheritdoc />
    public async Task AdvertiseRefsAsync(string mirrorPath, Stream output, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);
        ArgumentNullException.ThrowIfNull(output);

        // The prelude git clients expect before the advertisement itself: a pkt-line naming the service,
        // then a flush. Without it a client reports the response as not being a git server at all.
        await WritePacketLineAsync(output, "# service=git-upload-pack\n", ct);
        await output.WriteAsync("0000"u8.ToArray(), ct);

        await RunAsync(["upload-pack", "--stateless-rpc", "--advertise-refs", mirrorPath], null, output, ct);
    }

    /// <inheritdoc />
    public Task UploadPackAsync(string mirrorPath, Stream input, Stream output, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorPath);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        return RunAsync(["upload-pack", "--stateless-rpc", mirrorPath], input, output, ct);
    }

    /// <summary>
    ///     A pkt-line: four hex digits of total length, then the payload. Git's framing, and the reason the
    ///     prelude cannot simply be written as plain text.
    /// </summary>
    private static async Task WritePacketLineAsync(Stream output, string payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes((body.Length + 4).ToString("x4"));
        await output.WriteAsync(header, ct);
        await output.WriteAsync(body, ct);
    }

    /// <summary>
    ///     Removes any ambient git state from a child process's environment.
    ///     <para>
    ///         Git hooks export <c>GIT_DIR</c>, <c>GIT_INDEX_FILE</c>, and friends, and a child git process
    ///         inherits them. A server invoked from a hook would then operate on the hook's repository
    ///         instead of the one it was handed, which can rewrite an unrelated repository's index with
    ///         nothing to show it. The mirror path is passed explicitly, and nothing about the caller's
    ///         environment should be able to override it.
    ///     </para>
    /// </summary>
    private static void ScrubInheritedGitEnvironment(ProcessStartInfo startInfo)
    {
        string[] inherited =
        [
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_OBJECT_DIRECTORY",
            "GIT_COMMON_DIR", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_GLOBAL",
        ];

        foreach (var name in inherited)
        {
            startInfo.Environment.Remove(name);
        }
    }

    private static async Task RunAsync(
        IReadOnlyList<string> arguments,
        Stream? input,
        Stream output,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ScrubInheritedGitEnvironment(startInfo);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("git could not be started to serve a workspace fetch.");

        // Standard error is drained concurrently. A process whose error pipe fills stops writing to stdout,
        // which would look like a fetch that simply hangs.
        var errorDrain = process.StandardError.ReadToEndAsync(ct);

        if (input is not null)
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
            process.StandardInput.Close();
        }

        await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git upload-pack exited with {process.ExitCode}: {await errorDrain}");
        }
    }
}
