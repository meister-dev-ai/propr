// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Sign-in of provisioned users through the OpenID Connect identity provider that the installation configures for
///     them. A hosting platform provisions these users through the administration API, and the sign-in reaches only an
///     account that the platform provisioned.
/// </summary>
// Pre-authentication endpoints: the sign-in must be reachable without a user identity.
[AllowAnonymous]
[ApiController]
[Route("auth/provisioned-user")]
[TenantData(TenantDataAccess.None)]
public sealed class ProvisionedUserAuthController(
    IOptions<ProvisionedUserSsoOptions> ssoOptions,
    ISessionFactory sessionFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ProvisionedUserAuthController> logger,
    IProvisionedUserSignInService? signInService = null,
    ILicensingCapabilityService? licensingCapabilityService = null)
    : InstallationSignInController(sessionFactory, configuration, environment, logger, licensingCapabilityService)
{
    /// <inheritdoc />
    protected override bool IsProviderConfigured => ssoOptions.Value.IsConfigured;

    /// <inheritdoc />
    protected override IInstallationSignInService? SignInService => signInService;

    /// <inheritdoc />
    protected override ExternalSignInCookie FlowCookie => ExternalSignInCookie.ProvisionedUserFlow;

    /// <summary>
    ///     Starts the sign-in. ProPR sends the browser to the identity provider for provisioned users and keeps the state
    ///     of the sign-in in an httpOnly cookie.
    /// </summary>
    /// <param name="returnUrl">
    ///     The frontend page the sign-in returns to. Its origin is the origin of <c>MEISTER_PUBLIC_BASE_URL</c>, one of
    ///     <c>CORS_ORIGINS</c>, or without <c>MEISTER_PUBLIC_BASE_URL</c> the origin of this request.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [EnableRateLimiting("auth")]
    [HttpGet("challenge")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Challenge([FromQuery(Name = "returnUrl")] string? returnUrl, CancellationToken ct)
    {
        return this.ChallengeAsync(returnUrl, ct);
    }

    /// <summary>
    ///     Completes the sign-in at the redirect from the identity provider. When a hosting platform provisioned the
    ///     identity and the account holds a membership, ProPR sets the refresh cookie and returns the browser to the
    ///     frontend with the access token in the URL fragment. For every other identity, ProPR returns the browser to the
    ///     frontend with <c>error=no_access</c> and a fixed message in the fragment.
    /// </summary>
    [EnableRateLimiting("auth")]
    [HttpGet("callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Callback(
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "code")] string? code,
        [FromQuery(Name = "error")] string? error,
        [FromQuery(Name = "error_description")]
        string? errorDescription,
        CancellationToken ct)
    {
        return this.CallbackAsync(state, code, error, errorDescription, ct);
    }
}
