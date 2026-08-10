// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Decides whether a proxied call may be served. Every operation an executor makes against the control
///     plane goes through here first.
///     <para>
///         An executor is semi-trusted: it runs customer code analysis, but it is a host the control plane
///         does not own the lifecycle of, and a compromised or simply stale one must not be able to act on a
///         job it no longer holds. Authorising against the lease and its generation is what makes that
///         structural rather than a matter of the executor behaving well.
///     </para>
/// </summary>
public interface IRunnerCallAuthorizer
{
    /// <summary>
    ///     Authorizes a call against the lease the caller presents.
    /// </summary>
    /// <param name="call">The job, generation, and caller identity presented with the request.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerCallAuthorization> AuthorizeAsync(RunnerCallContext call, CancellationToken ct = default);
}
