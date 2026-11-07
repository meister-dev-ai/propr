// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>Authenticated session snapshot shared with the admin UI after sign-in.</summary>
/// <param name="GlobalRole">The global role of the caller, <c>Admin</c> or <c>User</c>.</param>
/// <param name="ClientRoles">The role of the caller on each client, keyed by the client id.</param>
/// <param name="TenantRoles">The role of the caller in each tenant, keyed by the tenant id.</param>
/// <param name="HasLocalPassword">Whether the account of the caller has a password for the password sign-in.</param>
/// <param name="Edition">The edition of the installation.</param>
/// <param name="Capabilities">The licensed capabilities and their state.</param>
/// <param name="ProCursorConfigured">
///     Whether the installation runs with ProCursor. While it runs without ProCursor, the console hides the ProCursor tab
///     and the ProCursor usage of each client, and the ProCursor endpoints of a client answer <c>404</c> with the code
///     <c>procursor_not_configured</c>.
/// </param>
public sealed record AuthenticatedSessionDto(
    string GlobalRole,
    Dictionary<string, int> ClientRoles,
    Dictionary<string, int> TenantRoles,
    bool HasLocalPassword,
    InstallationEdition Edition,
    IReadOnlyList<PremiumCapabilityDto> Capabilities,
    bool ProCursorConfigured);
