// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Authorizes an executor's fetch of a job's repository content and enforces the transfer ceiling.
/// </summary>
public sealed class RunnerWorkspaceServer(
    IRunnerCallAuthorizer authorizer,
    IRunnerWorkspaceRegistry registry,
    IRunnerWorkspaceSizeProbe sizeProbe) : IRunnerWorkspaceServer
{
    /// <inheritdoc />
    public async Task<RunnerWorkspaceGrant> AuthorizeFetchAsync(
        RunnerCallContext call,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Authorized against the lease and its generation like every other proxied operation. Repository
        // content is the largest thing an executor can ask for, so it is the last place to make an
        // exception for a caller that no longer holds the job.
        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return RunnerWorkspaceGrant.NotAuthorized(authorization.Refusal);
        }

        var source = registry.Find(call.JobId);
        if (source is null)
        {
            return RunnerWorkspaceGrant.NoMirror();
        }

        // Measured before anything is sent. A ceiling checked while streaming does not bound anything,
        // because by the time it trips the egress has already been paid for.
        var measured = await sizeProbe.MeasureAsync(source.MirrorPath, ct);
        if (measured > source.MaxTransferBytes)
        {
            return RunnerWorkspaceGrant.TooLarge(measured, source.MaxTransferBytes);
        }

        return RunnerWorkspaceGrant.Granted(source);
    }
}

/// <summary>
///     Holds each leased job's mirror. Process-local, like the tools and the budget: the mirror is a path on
///     this host's disk, so the replica that granted the lease is the one that can serve it.
///     <para>
///         It also owns the workspace behind the mirror. The workspace's disposal is what releases the
///         reference counts the cleanup sweeps honour, so a registry that only remembered the path would
///         keep two full checkouts on disk per dispatched job, forever.
///     </para>
/// </summary>
public sealed class RunnerWorkspaceRegistry : IRunnerWorkspaceRegistry
{
    private readonly ConcurrentDictionary<Guid, Entry> _byJob = new();

    /// <inheritdoc />
    public async ValueTask RegisterAsync(Guid jobId, RunnerWorkspaceSource source, IAsyncDisposable? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var replaced = this._byJob.TryGetValue(jobId, out var previous) ? previous : null;
        this._byJob[jobId] = new Entry(source, workspace);

        // A re-dispatch replaces the previous attempt's workspace. Its disk is released now, because the new
        // entry serves every fetch from this point on.
        if (replaced?.Workspace is not null && !ReferenceEquals(replaced.Workspace, workspace))
        {
            await DisposeQuietlyAsync(replaced.Workspace);
        }
    }

    /// <inheritdoc />
    public RunnerWorkspaceSource? Find(Guid jobId)
    {
        return this._byJob.TryGetValue(jobId, out var entry) ? entry.Source : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> RegisteredJobIds => [.. this._byJob.Keys];

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(Guid jobId)
    {
        if (this._byJob.TryRemove(jobId, out var entry) && entry.Workspace is not null)
        {
            await DisposeQuietlyAsync(entry.Workspace);
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable workspace)
    {
        try
        {
            await workspace.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a directory that cannot be deleted right now is the cleanup sweep's to retry,
            // and failing the release path over it would strand the registry entry too.
            _ = ex;
        }
    }

    private sealed record Entry(RunnerWorkspaceSource Source, IAsyncDisposable? Workspace);
}
