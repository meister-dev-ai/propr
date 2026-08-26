// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Catalog entry describing one premium capability.
///     <para>
///         The three messages cover the ways a capability itself can be unavailable. They are written per
///         capability rather than composed from <paramref name="DisplayName" /> because the wording has to agree
///         with the name in number. The remaining members of
///         <see cref="PremiumCapabilityUnavailableReason" /> describe the installation's license term rather than
///         a capability, so their wording lives with capability resolution instead of here.
///     </para>
/// </summary>
/// <param name="Key">The stable capability key, as a license names it.</param>
/// <param name="DisplayName">The name an operator sees.</param>
/// <param name="CommercialRequiredMessage">Shown when the installation has no license whose term is running.</param>
/// <param name="NotInLicenseMessage">Shown when a license is in force but does not name this capability.</param>
/// <param name="CommercialDisabledMessage">Shown when the capability is licensed but turned off by an override.</param>
/// <param name="RequiresCommercial">Whether the capability needs a license at all.</param>
public sealed record PremiumCapabilityDefinition(
    string Key,
    string DisplayName,
    string CommercialRequiredMessage,
    string NotInLicenseMessage,
    string CommercialDisabledMessage,
    bool RequiresCommercial = true);
