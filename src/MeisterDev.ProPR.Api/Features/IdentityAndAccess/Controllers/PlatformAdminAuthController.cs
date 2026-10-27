// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
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
    UsageStatisticsService? usageStatisticsService = null) : ControllerBase
{
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
    public async Task<IActionResult> Challenge([FromQuery(Name = "returnUrl")] string? returnUrl, CancellationToken ct)
    {
        if (await this.RefuseUnavailableProviderAsync(ct) is { } refusal)
        {
            return refusal;
        }

        var frontendReturnUrl = this.TryResolveReturnUrl(returnUrl);
        if (frontendReturnUrl is null)
        {
            return this.BadRequest(new { error = "returnUrl must be an absolute URL on the origin that serves the user interface of this installation." });
        }

        var challenge = await signInService!.BuildChallengeAsync(this.GetApplicationBaseUri(), frontendReturnUrl, ct);
        var providerRedirectUri = challenge is null
            ? null
            : ExternalSignInRedirects.TryResolveProviderRedirectUri(challenge.RedirectUrl, challenge.AllowedRedirectOrigin);
        if (challenge is null || providerRedirectUri is null)
        {
            logger.LogWarning("PlatformAdminSignInRefused Reason={Reason}", "provider_unavailable");
            return this.RedirectWithFailure(
                frontendReturnUrl.ToString(),
                "provider_unavailable",
                "The identity provider for platform administrators cannot be reached. Sign in with a local account or try again later.");
        }

        ExternalSignInCookie.PlatformAdminFlow.Set(this.Response, challenge.ProtectedFlow, this.Request.IsHttps);
        return ExternalSignInRedirects.RedirectTo(this.Response, providerRedirectUri);
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
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "code")] string? code,
        [FromQuery(Name = "error")] string? error,
        [FromQuery(Name = "error_description")]
        string? errorDescription,
        CancellationToken ct)
    {
        if (await this.RefuseUnavailableProviderAsync(ct) is { } refusal)
        {
            return refusal;
        }

        var protectedFlow = ExternalSignInCookie.PlatformAdminFlow.Read(this.Request);
        ExternalSignInCookie.PlatformAdminFlow.Clear(this.Response, this.Request.IsHttps);

        var completion = await signInService!.CompleteSignInAsync(
            this.GetApplicationBaseUri(),
            new PlatformAdminSignInCallback(state, code, error, errorDescription),
            protectedFlow,
            ct);

        if (completion.User is null)
        {
            var failureCode = completion.FailureCode ?? "external_identity_rejected";
            var failureMessage = completion.FailureMessage ?? "The identity provider sign-in was refused.";
            return this.RedirectWithFailure(completion.FrontendReturnUrl, failureCode, failureMessage);
        }

        // The return page was checked when the sign-in started. A page whose origin no longer qualifies receives no
        // session.
        var returnUri = this.TryResolveReturnUrl(completion.FrontendReturnUrl);
        if (returnUri is null)
        {
            return this.Unauthorized(new { error = "return_url_not_allowed", message = "The sign-in cannot return to the page it started from." });
        }

        var session = await sessionFactory.CreateAsync(completion.User, ct);
        var successfulRedirectUri = ExternalSignInRedirects.WithFragment(
            returnUri,
            new Dictionary<string, string?>
            {
                ["accessToken"] = session.AccessToken,
                ["expiresIn"] = session.ExpiresIn.ToString(CultureInfo.InvariantCulture),
                ["tokenType"] = session.TokenType,
            });

        // The refresh token goes only into the httpOnly cookie, never into the URL fragment, which a browser keeps
        // in its history.
        RefreshTokenCookie.Set(this.Response, session.RefreshToken, session.RefreshTokenExpiresAt, this.Request.IsHttps);
        await AdministratorSignIns.RecordAsync(usageStatisticsService, completion.User, ct);
        return ExternalSignInRedirects.RedirectTo(this.Response, successfulRedirectUri);
    }

    /// <summary>
    ///     Answers for a provider that is not in use: <c>404</c> when the installation configured none or runs without a
    ///     database, <c>409</c> when the license does not grant single sign-on.
    /// </summary>
    private async Task<IActionResult?> RefuseUnavailableProviderAsync(CancellationToken ct)
    {
        if (!ssoOptions.Value.IsConfigured || signInService is null || licensingCapabilityService is null)
        {
            return this.NotFound();
        }

        var capability = await licensingCapabilityService.GetCapabilityAsync(PremiumCapabilityKey.SsoAuthentication, ct);
        return capability.IsAvailable ? null : new PremiumFeatureUnavailableResult(capability);
    }

    private IActionResult RedirectWithFailure(string? frontendReturnUrl, string failureCode, string failureMessage)
    {
        var returnUri = this.TryResolveReturnUrl(frontendReturnUrl);
        return returnUri is null
            ? this.Unauthorized(new { error = failureCode, message = failureMessage })
            : ExternalSignInRedirects.RedirectTo(
                this.Response,
                ExternalSignInRedirects.WithFragment(
                    returnUri,
                    new Dictionary<string, string?>
                    {
                        ["error"] = failureCode,
                        ["message"] = failureMessage,
                    }));
    }

    private Uri? TryResolveReturnUrl(string? returnUrl)
    {
        return ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(
            returnUrl,
            this.Request,
            configuration,
            environment.IsDevelopment());
    }

    private Uri GetApplicationBaseUri()
    {
        return PublicApplicationUrlResolver.GetApplicationBaseUri(this.Request, configuration);
    }
}
