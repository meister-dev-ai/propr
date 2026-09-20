// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Whether the installation is licensed for the capability a provider family declared.
/// </summary>
/// <remarks>
///     Asked on the path a review takes, so the answer has to be available without a database read per outbound
///     request.
/// </remarks>
public interface IProviderAddInCapabilityGate
{
    /// <summary>Whether the capability is currently available.</summary>
    /// <param name="capabilityKey">The key the family declared, or null for a family that declared none.</param>
    /// <param name="ct">Cancels the check.</param>
    ValueTask<bool> IsAvailableAsync(string? capabilityKey, CancellationToken ct = default);

    /// <summary>The same answer, resolved now rather than taken from the held one.</summary>
    /// <remarks>
    ///     Asked where the answer authorizes a write rather than a use: a credential an action produced is stored
    ///     minutes after the action started, and a held answer from before it started would let one be written
    ///     against an entitlement that has since lapsed. The resolved answer replaces the held one.
    /// </remarks>
    /// <param name="capabilityKey">The key the family declared, or null for a family that declared none.</param>
    /// <param name="ct">Cancels the check.</param>
    ValueTask<bool> IsAvailableNowAsync(string? capabilityKey, CancellationToken ct = default);
}
