// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Sign-in of platform administrators through the OpenID Connect identity provider configured for the
///     installation. The local sign-in at <c>POST /auth/login</c> stays available beside it.
/// </summary>
// Pre-authentication endpoints: the sign-in must be reachable without a user identity.
[AllowAnonymous]
[ApiController]
[Route("auth/platform-admin")]
[TenantData(TenantDataAccess.None)]
public sealed class PlatformAdminAuthController(
    IOptions<PlatformAdminSsoOptions> ssoOptions,
    ISessionFactory sessionFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<PlatformAdminAuthController> logger,
    IPlatformAdminSignInService? signInService = null,
    ILicensingCapabilityService? licensingCapabilityService = null,
    UsageStatisticsService? usageStatisticsService = null)
    : InstallationSignInController(sessionFactory, configuration, environment, logger, licensingCapabilityService)
{
    /// <inheritdoc />
    protected override bool IsProviderConfigured => ssoOptions.Value.IsConfigured;

    /// <inheritdoc />
    protected override IInstallationSignInService? SignInService => signInService;

    /// <inheritdoc />
    protected override ExternalSignInCookie FlowCookie => ExternalSignInCookie.PlatformAdminFlow;

    /// <summary>
    ///     Starts the sign-in. ProPR sends the browser to the identity provider and keeps the state of the sign-in in an
    ///     httpOnly cookie.
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
    ///     Completes the sign-in at the redirect from the identity provider. On success it sets the refresh cookie and
    ///     returns the browser to the frontend with the access token in the URL fragment. A refused sign-in returns to
    ///     the frontend with <c>error</c> and <c>message</c> in the fragment.
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

    /// <summary>Records the sign-in of a platform administrator for the usage-statistics consent gate.</summary>
    protected override Task OnSignedInAsync(AppUser user, CancellationToken ct)
    {
        return AdministratorSignIns.RecordAsync(usageStatisticsService, user, ct);
    }
}
