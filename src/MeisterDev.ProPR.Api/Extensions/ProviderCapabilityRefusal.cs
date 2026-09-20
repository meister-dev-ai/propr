// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Extensions;

/// <summary>
///     Whether this installation is licensed for the capability a provider family declared, checked where a
///     connection using that family is written.
/// </summary>
/// <remarks>
///     <para>
///         A family names the capability key it requires as an opaque string and carries no licensing code. The
///         host checks that key against the licence at four points, and this is the first of them: before a
///         connection profile using the family is saved, so an installation that is not entitled is told while
///         the form is still open rather than at the first review.
///     </para>
///     <para>
///         The other three are the dispatch of one of the family's actions, the store of a credential one of its
///         actions produced, and the resolution of a stored credential for use. The last is the one the others do
///         not cover: it is the only one on the review path, and without it an installation whose entitlement
///         lapses keeps running reviews on credentials it already holds.
///     </para>
///     <para>
///         A family that declares no capability requires none, which is every family this build ships.
///     </para>
/// </remarks>
public static class ProviderCapabilityRefusal
{
    /// <summary>
    ///     Why this installation may not use <paramref name="providerKind" />, or <see langword="null" /> when it
    ///     may.
    /// </summary>
    /// <param name="drivers">Resolves the family, whose declaration names the capability.</param>
    /// <param name="capabilities">The licence check, absent in a composition that has no licensing state.</param>
    /// <param name="providerKind">The family the connection would use.</param>
    /// <param name="ct">Cancels the check.</param>
    public static async Task<string?> DescribeAsync(
        IAiProviderDriverRegistry drivers,
        IProviderAddInCapabilityGate? capabilities,
        string providerKind,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        if (capabilities is null || !drivers.IsRegistered(providerKind))
        {
            return null;
        }

        var declaration = drivers.GetRequired(providerKind).Declaration;
        if (declaration.RequiredCapabilityKey is not { Length: > 0 } capabilityKey)
        {
            return null;
        }

        return await capabilities.IsAvailableAsync(capabilityKey, ct).ConfigureAwait(false)
            ? null
            : $"the '{declaration.Label}' provider family requires the '{capabilityKey}' capability, which this "
              + "installation's licence does not currently make available";
    }
}
