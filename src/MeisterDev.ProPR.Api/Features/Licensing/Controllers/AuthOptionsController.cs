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
    IOptions<InstallationSignInOptions> signInOptions,
    ILicensingCapabilityService? licensingCapabilityService = null)
    : ControllerBase
{
    /// <summary>
    ///     Returns public sign-in options and edition-aware capability messaging. <c>availableSignInMethods</c> contains
    ///     <c>password</c> while the installation offers the password sign-in, and <c>sso</c> while the license grants
    ///     single sign-on and the installation offers the identity providers of tenants.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AuthOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthOptions(CancellationToken ct = default)
    {
        var publicBaseUrl = PublicApplicationUrlResolver.GetApplicationBaseUri(this.Request, configuration)
            .AbsoluteUri
            .TrimEnd('/');

        if (licensingCapabilityService is null)
        {
            return this.Ok(new AuthOptionsDto(InstallationEdition.Community, this.OfferedSignInMethods(["password"]), [], publicBaseUrl));
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
                AvailableSignInMethods = this.OfferedSignInMethods(options.AvailableSignInMethods),
                PublicBaseUrl = publicBaseUrl,
                PlatformAdminSignIn = isLicensed && platformAdmin.IsConfigured
                    ? new PlatformAdminSignInOptionDto(platformAdmin.EffectiveDisplayName)
                    : null,
                ProvisionedUserSignIn = isLicensed && provisionedUser.IsConfigured
                    ? new ProvisionedUserSignInOptionDto(provisionedUser.EffectiveDisplayName)
                    : null,
            });
    }

    // The license decides which sign-in methods exist, and the installation can turn off the password sign-in and the
    // identity providers of tenants. The identity providers of the installation have fields of their own.
    private List<string> OfferedSignInMethods(IEnumerable<string> licensedMethods)
    {
        var signIns = signInOptions.Value;
        return licensedMethods
            .Where(method => method switch
            {
                "password" => signIns.LocalSignInEnabled,
                "sso" => signIns.TenantSsoEnabled,
                _ => true,
            })
            .ToList();
    }
}
