// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Whether a job's model reasoning may be recorded, answered on the control plane.
///     <para>
///         A runner decides nothing here. It states what it wants on the relay and it spools what it produced,
///         and both arrive on the control plane, which applies this answer before the text is sent for or
///         stored. An outdated or hostile runner therefore cannot land reasoning a tenant has forbidden.
///     </para>
/// </summary>
public interface IRunnerJobReasoningCapturePolicy
{
    /// <summary>
    ///     Whether the job may record reasoning: the policy of the tenant owning its client, or the
    ///     installation switch where that tenant states none. A job that cannot be resolved to a client
    ///     answers "do not capture": no tenant has permitted the text, and an invalid or half-written job
    ///     must not be the reason reasoning is stored.
    /// </summary>
    /// <param name="jobId">The review job.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CapturesReasoningAsync(Guid jobId, CancellationToken ct = default);
}
