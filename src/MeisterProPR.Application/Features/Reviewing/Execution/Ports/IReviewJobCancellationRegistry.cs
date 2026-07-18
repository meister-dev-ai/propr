// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Process-local registry of cancellation sources for the review jobs currently executing on this
///     instance. It lets a control-plane stop request interrupt in-flight work promptly on the instance
///     running the job. The persisted job status remains the cross-instance source of truth: an instance
///     that is not running the job simply finds no entry here and relies on the status checkpoints in the
///     review pipeline to abort.
/// </summary>
public interface IReviewJobCancellationRegistry
{
    /// <summary>
    ///     Registers a fresh cancellation source for the job and returns its token. Any previously
    ///     registered source for the same job is discarded first. The worker passes the returned token
    ///     into review execution and inspects it to tell a manual stop apart from host shutdown.
    /// </summary>
    CancellationToken Register(Guid jobId);

    /// <summary>
    ///     Signals cancellation for the job if it is currently registered on this instance.
    ///     Returns <see langword="true" /> if a registration was found and signalled.
    /// </summary>
    bool Cancel(Guid jobId);

    /// <summary>Removes and disposes the job's cancellation source. Safe to call when none is registered.</summary>
    void Remove(Guid jobId);
}
