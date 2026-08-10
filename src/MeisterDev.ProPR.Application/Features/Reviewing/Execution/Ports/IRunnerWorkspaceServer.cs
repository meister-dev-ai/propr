// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Decides whether an executor may fetch a job's repository content, and from where.
///     <para>
///         Repository content is the one thing an executor needs in bulk and the one thing it cannot fetch
///         itself, because cloning is a credentialed operation. The control plane already holds a mirror it
///         fetched with its own credentials, so it serves that mirror over the git wire protocol instead of
///         handing out a token.
///     </para>
///     <para>
///         This is the decision, not the transfer. Authorization, the mirror to serve, and the ceiling are
///         settled here; moving the bytes is the transport's job.
///     </para>
/// </summary>
public interface IRunnerWorkspaceServer
{
    /// <summary>
    ///     Authorizes a fetch and returns what the executor may take, or why it may not.
    /// </summary>
    /// <param name="call">The caller's job, lease generation, and identity.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerWorkspaceGrant> AuthorizeFetchAsync(RunnerCallContext call, CancellationToken ct = default);
}

/// <summary>
///     The mirror held open for a leased job, and the ceiling on what may be transferred from it.
/// </summary>
public interface IRunnerWorkspaceRegistry
{
    /// <summary>
    ///     Holds a job's mirror — and the workspace behind it — for the life of its lease. The registry
    ///     owns the workspace from here: nothing else disposes it, and a registration that replaces an
    ///     earlier one for the same job releases the replaced workspace's disk.
    /// </summary>
    ValueTask RegisterAsync(Guid jobId, RunnerWorkspaceSource source, IAsyncDisposable? workspace = null);

    /// <summary>Returns the mirror held for a job, or null when none is.</summary>
    RunnerWorkspaceSource? Find(Guid jobId);

    /// <summary>The jobs this replica currently holds workspaces for.</summary>
    IReadOnlyList<Guid> RegisteredJobIds { get; }

    /// <summary>
    ///     Drops the mirror held for a job and disposes the workspace behind it. Without this the
    ///     workspace's reference counts never fall, the cleanup sweeps skip its directories forever, and
    ///     the replica accumulates two full checkouts per dispatched job until the disk is gone.
    /// </summary>
    ValueTask ReleaseAsync(Guid jobId);
}

/// <summary>
///     Serves a bare mirror over git's smart HTTP protocol.
///     <para>
///         Streamed, not buffered: a pack for a real repository is hundreds of megabytes, and holding one
///         per concurrent runner in memory is the opposite of the isolation this feature exists to buy.
///     </para>
/// </summary>
public interface IGitUploadPackTransport
{
    /// <summary>Writes the ref advertisement a git client reads first.</summary>
    Task AdvertiseRefsAsync(string mirrorPath, Stream output, CancellationToken ct);

    /// <summary>Serves the client's want/have negotiation and streams back the pack.</summary>
    Task UploadPackAsync(string mirrorPath, Stream input, Stream output, CancellationToken ct);
}

/// <summary>Measures what a fetch would cost, so a ceiling can be enforced before anything is sent.</summary>
public interface IRunnerWorkspaceSizeProbe
{
    /// <summary>The size in bytes of the repository content at the given path.</summary>
    Task<long> MeasureAsync(string mirrorPath, CancellationToken ct = default);
}
