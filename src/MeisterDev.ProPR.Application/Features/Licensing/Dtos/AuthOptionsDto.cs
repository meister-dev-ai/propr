// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>Public authentication bootstrap information for the current installation edition.</summary>
/// <param name="Edition">The edition of the installation.</param>
/// <param name="AvailableSignInMethods">
///     The sign-in methods the installation offers: <c>password</c> while the installation offers the password sign-in,
///     and <c>sso</c> while the license grants single sign-on and the installation offers the identity providers of
///     tenants.
/// </param>
/// <param name="Capabilities">The licensed capabilities and their state.</param>
/// <param name="PublicBaseUrl">The public base URL of the API.</param>
/// <param name="PlatformAdminSignIn">
///     The identity provider through which platform administrators can sign in, or <see langword="null" /> when the
///     installation configured none or its license does not grant single sign-on.
/// </param>
/// <param name="ProvisionedUserSignIn">
///     The identity provider through which provisioned users can sign in, or <see langword="null" /> when the
///     installation configured none or its license does not grant single sign-on.
/// </param>
public sealed record AuthOptionsDto(
    InstallationEdition Edition,
    IReadOnlyList<string> AvailableSignInMethods,
    IReadOnlyList<PremiumCapabilityDto> Capabilities,
    string? PublicBaseUrl = null,
    PlatformAdminSignInOptionDto? PlatformAdminSignIn = null,
    ProvisionedUserSignInOptionDto? ProvisionedUserSignIn = null);

/// <summary>The identity provider through which platform administrators can sign in.</summary>
/// <param name="DisplayName">The name of the provider on the sign-in page.</param>
public sealed record PlatformAdminSignInOptionDto(string DisplayName);

/// <summary>The identity provider through which the users that a hosting platform provisioned can sign in.</summary>
/// <param name="DisplayName">The name of the provider on the sign-in page.</param>
public sealed record ProvisionedUserSignInOptionDto(string DisplayName);
