// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Resolves everything non-secret a review needs into a job manifest, once, when the job is dispatched.
///     <para>
///         Configuration is otherwise read from the database at a dozen points during a review. An executor
///         without database access cannot do that at all, and reading it progressively has a second problem
///         that applies just as much in-process: a configuration change made while a review is running
///         alters a review already under way, so the same job behaves as two different jobs depending on
///         when each value was read.
///     </para>
/// </summary>
public interface IRunnerJobManifestResolver
{
    /// <summary>
    ///     Builds the manifest for a leased job. Either every value is resolved or none is: a half-populated
    ///     manifest would have an executor review under configuration that was never chosen.
    /// </summary>
    /// <param name="request">The leased job and the frozen scope it was dispatched for.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerJobManifestResolution> ResolveAsync(RunnerJobManifestRequest request, CancellationToken ct = default);
}
