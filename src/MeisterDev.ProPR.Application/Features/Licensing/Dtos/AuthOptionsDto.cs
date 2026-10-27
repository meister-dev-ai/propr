// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>Public authentication bootstrap information for the current installation edition.</summary>
/// <param name="Edition">The edition of the installation.</param>
/// <param name="AvailableSignInMethods">The sign-in methods the installation offers.</param>
/// <param name="Capabilities">The licensed capabilities and their state.</param>
/// <param name="PublicBaseUrl">The public base URL of the API.</param>
/// <param name="PlatformAdminSignIn">
///     The identity provider through which platform administrators can sign in, or <see langword="null" /> when the
///     installation configured none or its license does not grant single sign-on.
/// </param>
public sealed record AuthOptionsDto(
    InstallationEdition Edition,
    IReadOnlyList<string> AvailableSignInMethods,
    IReadOnlyList<PremiumCapabilityDto> Capabilities,
    string? PublicBaseUrl = null,
    PlatformAdminSignInOptionDto? PlatformAdminSignIn = null);

/// <summary>The identity provider through which platform administrators can sign in.</summary>
/// <param name="DisplayName">The name of the provider on the sign-in page.</param>
public sealed record PlatformAdminSignInOptionDto(string DisplayName);
