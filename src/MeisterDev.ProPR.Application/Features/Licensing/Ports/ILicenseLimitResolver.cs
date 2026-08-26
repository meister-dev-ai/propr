// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     What one quota is currently held to on this installation.
///     <para>
///         Enforcement asks this rather than reading limit values off the license itself, so client creation,
///         runner enrollment and the concurrent-review claim all answer from one place. The concurrent-review
///         path additionally consults the parallel-review-execution capability, whose check performs its own
///         license state read, bounded by the cache window the license state provider holds a state for.
///     </para>
/// </summary>
public interface ILicenseLimitResolver
{
    /// <summary>Resolves the effective ceiling for one quota.</summary>
    /// <param name="key">The dimension to resolve.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The ceiling, where it came from, and the stage it was resolved under.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key is not one this resolver knows.</exception>
    Task<LicenseLimitResolution> ResolveAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default);
}
