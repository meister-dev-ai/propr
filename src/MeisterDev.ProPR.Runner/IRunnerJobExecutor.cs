// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     Runs one leased job to completion.
///     <para>
///         A port rather than a concrete call so the loop, which owns capacity, backoff, and draining, can
///         be exercised without a repository, a control plane, or a model. Those are the parts an operator
///         depends on being right when everything else is broken.
///     </para>
/// </summary>
public interface IRunnerJobExecutor
{
    /// <summary>
    ///     Executes the job the manifest describes. Returning means the job reached a terminal state and
    ///     its results were shipped; throwing means it did not, and the loop hands the lease back so
    ///     another runner can pick the job up rather than leaving it to expire. Cancellation is not a
    ///     failure: the drain releases what it holds, so this must not also hand the lease back.
    /// </summary>
    /// <param name="manifest">Everything non-secret the review needs, resolved once at dispatch.</param>
    /// <param name="ct">
    ///     Cancelled when the host is draining. Lease-loss cancellation is not wired yet: nothing renews a
    ///     lease from this host, so there is no renewal failure to observe. It arrives with the heartbeat.
    /// </param>
    Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct);
}
