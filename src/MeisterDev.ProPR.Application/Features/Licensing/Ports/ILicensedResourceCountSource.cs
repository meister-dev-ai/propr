// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Reads what the installation currently holds for the dimensions a license states limits for.
///     <para>
///         The implementation answers from counts that are already cheap to take. It is a reporting read, so a
///         caller must not treat what it returns as a quota decision.
///     </para>
///     <para>
///         Enforcement of a runner ceiling has to consume this same source rather than counting runners its
///         own way. Two counting rules would let the number an administrator reads differ from the number an
///         enrollment is refused against, and the disagreement would surface as a refusal the panel cannot
///         explain.
///     </para>
/// </summary>
public interface ILicensedResourceCountSource
{
    /// <summary>Returns the current counts.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The counts.</returns>
    Task<LicensedResourceCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}
