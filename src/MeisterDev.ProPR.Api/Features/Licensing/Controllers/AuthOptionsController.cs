// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Features.Licensing.Controllers;

/// <summary>Public bootstrap endpoints for login-time licensing awareness.</summary>
// Serves sign-in options before the user has any credentials, so it must be reachable anonymously.
[AllowAnonymous]
[ApiController]
[Route("auth/options")]
[TenantData(TenantDataAccess.None)]
public sealed class AuthOptionsController(
    IConfiguration configuration,
    IOptions<PlatformAdminSsoOptions> platformAdminSsoOptions,
    IOptions<ProvisionedUserSsoOptions> provisionedUserSsoOptions,
    ILicensingCapabilityService? licensingCapabilityService = null)
    : ControllerBase
{
    /// <summary>Returns public sign-in options and edition-aware capability messaging.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AuthOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthOptions(CancellationToken ct = default)
    {
        var publicBaseUrl = PublicApplicationUrlResolver.GetApplicationBaseUri(this.Request, configuration)
            .AbsoluteUri
            .TrimEnd('/');

        if (licensingCapabilityService is null)
        {
            return this.Ok(new AuthOptionsDto(InstallationEdition.Community, ["password"], [], publicBaseUrl));
        }

        var options = await licensingCapabilityService.GetAuthOptionsAsync(ct);

        // The identity providers of the installation are offered when the installation configured them and the license
        // grants single sign-on. Without the license, ProPR ignores them, and the sign-in page offers the local sign-in
        // only.
        var isLicensed = options.Capabilities.Any(capability =>
            string.Equals(capability.Key, PremiumCapabilityKey.SsoAuthentication, StringComparison.OrdinalIgnoreCase)
            && capability.IsAvailable);
        var platformAdmin = platformAdminSsoOptions.Value;
        var provisionedUser = provisionedUserSsoOptions.Value;
        return this.Ok(
            options with
            {
                PublicBaseUrl = publicBaseUrl,
                PlatformAdminSignIn = isLicensed && platformAdmin.IsConfigured
                    ? new PlatformAdminSignInOptionDto(platformAdmin.EffectiveDisplayName)
                    : null,
                ProvisionedUserSignIn = isLicensed && provisionedUser.IsConfigured
                    ? new ProvisionedUserSignInOptionDto(provisionedUser.EffectiveDisplayName)
                    : null,
            });
    }
}
