// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Where the installation's activated license stands right now.
///     <para>
///         The stored document is verified on every load. A build whose trust anchor no longer accepts it, and
///         a license whose term has ended, therefore both show up without an operator action.
///     </para>
/// </summary>
public interface ILicenseStateProvider
{
    /// <summary>Reads the current license state, loading and verifying the stored document when needed.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state.</returns>
    Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Discards what this process holds, so the next read loads and verifies again. It reaches this
    ///     process only: other replicas pick up a change when their own copy ages out.
    /// </summary>
    void Invalidate();
}
