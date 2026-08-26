// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Observes the system the installation is running on and records the profile it amounts to.
///     <para>
///         The profile is descriptive. Nothing in verification, resolution, activation or quota enforcement
///         reads it, and an observation that fails takes nothing with it.
///     </para>
/// </summary>
public interface ISystemProfileObserver
{
    /// <summary>
    ///     Observes the system and records what it found: the first observation captures the profile, a later
    ///     one that finds the stable components changed records the drift, and one that finds them unchanged
    ///     refreshes the volatile components without recording anything.
    ///     <para>
    ///         It reports its own failures through the log and does not throw, so a caller can observe as a step
    ///         of something else without wrapping it.
    ///     </para>
    /// </summary>
    /// <param name="identityCreatedAt">
    ///     When the installation's licensing identity was created, which is one of the stable components. The
    ///     caller supplies it because it is reading or writing that row already.
    /// </param>
    /// <param name="cancellationToken">Cancels the observation.</param>
    /// <returns>A task that completes when the observation has been recorded or reported as failed.</returns>
    Task ObserveAsync(DateTimeOffset identityCreatedAt, CancellationToken cancellationToken = default);
}
