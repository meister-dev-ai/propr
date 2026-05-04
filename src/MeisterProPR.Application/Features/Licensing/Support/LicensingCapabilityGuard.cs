// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Application.Features.Licensing.Support;

/// <summary>Convenience helpers for resolving whether a premium capability should block the current operation.</summary>
public static class LicensingCapabilityGuard
{
    /// <summary>Returns the blocking capability snapshot when the requested capability is unavailable; otherwise returns <see langword="null" />.</summary>
    public static async Task<CapabilitySnapshot?> GetUnavailableCapabilityAsync(
        ILicensingCapabilityService? licensingCapabilityService,
        string capabilityKey,
        CancellationToken cancellationToken = default)
    {
        if (licensingCapabilityService is null)
        {
            return null;
        }

        var capability = await licensingCapabilityService.GetCapabilityAsync(capabilityKey, cancellationToken);
        return capability.IsAvailable ? null : capability;
    }

    /// <summary>
    ///     Returns the first unavailable capability only when every supplied capability is unavailable.
    ///     Shared helper endpoints can use this to remain accessible when at least one premium feature still needs them.
    /// </summary>
    public static async Task<CapabilitySnapshot?> GetUnavailableCapabilityAsync(
        ILicensingCapabilityService? licensingCapabilityService,
        IReadOnlyList<string> capabilityKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityKeys);

        if (licensingCapabilityService is null || capabilityKeys.Count == 0)
        {
            return null;
        }

        CapabilitySnapshot? firstUnavailable = null;
        foreach (var capabilityKey in capabilityKeys)
        {
            var capability = await licensingCapabilityService.GetCapabilityAsync(capabilityKey, cancellationToken);
            if (capability.IsAvailable)
            {
                return null;
            }

            firstUnavailable ??= capability;
        }

        return firstUnavailable;
    }
}
