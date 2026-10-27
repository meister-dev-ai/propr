// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;
using System.Globalization;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Tenant-scoped login discovery and sign-in endpoints.</summary>
// Pre-authentication endpoints: login discovery and sign-in must be reachable without a user identity.
[AllowAnonymous]
[ApiController]
[Route("auth")]
[TenantData(TenantDataAccess.None)]
public sealed class TenantAuthController(
    ITenantAuthService tenantAuthService,
    ISessionFactory sessionFactory,
    ILogger<TenantAuthController> logger,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const string ReturnUrlNotAllowed = "return_url_not_allowed";

    private const string ReturnUrlNotAllowedMessage =
        "returnUrl must be an absolute URL on the origin that serves the user interface of this installation.";

    private async Task<CapabilitySnapshot?> GetUnavailableTenantSsoCapabilityAsync(CancellationToken ct)
    {
        return await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.SsoAuthentication,
            ct);
    }

    private async Task<IActionResult?> RequireTenantSsoCapabilityAsync(CancellationToken ct)
    {
        var capability = await this.GetUnavailableTenantSsoCapabilityAsync(ct);
        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }

    /// <summary>Returns the visible login options for one tenant sign-in route.</summary>
    [HttpGet("tenants/{tenantSlug}/providers")]
    [ProducesResponseType(typeof(TenantLoginOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviders(string tenantSlug, CancellationToken ct)
    {
        var options = await tenantAuthService.GetLoginOptionsAsync(tenantSlug, ct);
        if (options is null)
        {
            return this.NotFound();
        }

        var unavailableCapability = await this.GetUnavailableTenantSsoCapabilityAsync(ct);
        if (unavailableCapability is not null)
        {
            return this.Ok(options with { Providers = [] });
        }

        return this.Ok(options);
    }

    /// <summary>Signs a tenant user in with local credentials when tenant policy allows it.</summary>
    [EnableRateLimiting("auth")]
    [HttpPost("tenants/{tenantSlug}/local-login")]
    [ProducesResponseType(typeof(TenantAuthSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LocalLogin(
        string tenantSlug,
        [FromBody] TenantLocalLoginRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.BadRequest(new { error = "Username and password are required." });
        }

        var user = await tenantAuthService.AuthenticateLocalAsync(tenantSlug, request.Username, request.Password, ct);
        if (user is null)
        {
            var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            var safeUsername = request.Username.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            logger.LogWarning(
                "TenantLocalLoginRejected TenantSlug={TenantSlug} Username={Username}",
                safeTenantSlug,
                safeUsername);
            return this.Unauthorized(new { error = "Invalid credentials or tenant policy denial." });
        }

        var session = await sessionFactory.CreateAsync(user!, ct);
        RefreshTokenCookie.Set(this.Response, session.RefreshToken, session.RefreshTokenExpiresAt, this.Request.IsHttps);
        return this.Ok(AuthHelpers.ToTenantAuthSessionDto(session));
    }

    /// <summary>
    ///     Starts the tenant-scoped external sign-in flow for the selected provider. ProPR sends the browser to the provider
    ///     with a PKCE code challenge, and with a nonce for an OpenID Connect provider, and keeps the state of the sign-in
    ///     in an httpOnly cookie.
    /// </summary>
    /// <param name="tenantSlug">The tenant of the provider.</param>
    /// <param name="providerId">The provider to sign in with.</param>
    /// <param name="returnUrl">
    ///     The frontend page the sign-in returns to. Its origin is the origin of <c>MEISTER_PUBLIC_BASE_URL</c>, one of
    ///     <c>CORS_ORIGINS</c>, or without <c>MEISTER_PUBLIC_BASE_URL</c> the origin of this request. Without it, the
    ///     callback answers with JSON.
    /// </param>
    /// <param name="mode">
    ///     <c>signIn</c>, the default, signs the person in. <c>link</c> signs nobody in and keeps the identity as a pending
    ///     link for the account that is signed in when the person confirms the link; it needs <paramref name="returnUrl" />.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [EnableRateLimiting("auth")]
    [HttpGet("external/challenge/{tenantSlug}/{providerId:guid}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChallengeExternal(
        string tenantSlug,
        Guid providerId,
        [FromQuery(Name = "returnUrl")] string? returnUrl,
        [FromQuery(Name = "mode")] string? mode,
        CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        if (!TryParseMode(mode, out var signInMode))
        {
            return this.BadRequest(new { error = "invalid_mode", message = "mode must be signIn or link." });
        }

        Uri? frontendReturnUrl = null;
        if (!string.IsNullOrEmpty(returnUrl))
        {
            frontendReturnUrl = this.TryResolveReturnUrl(returnUrl);
            if (frontendReturnUrl is null)
            {
                return this.BadRequest(new { error = ReturnUrlNotAllowed, message = ReturnUrlNotAllowedMessage });
            }
        }
        else if (signInMode == TenantExternalSignInMode.Link)
        {
            return this.BadRequest(new { error = "return_url_required", message = "A sign-in that links an account needs the page it returns to." });
        }

        var challenge = await tenantAuthService.BuildExternalChallengeAsync(
            tenantSlug,
            providerId,
            this.GetApplicationBaseUri(),
            frontendReturnUrl,
            signInMode,
            ct);
        if (challenge is null)
        {
            return this.NotFound();
        }

        var challengeRedirectUri = ExternalSignInRedirects.TryResolveProviderRedirectUri(
            challenge.RedirectUrl,
            challenge.AllowedRedirectOrigin);
        if (challengeRedirectUri is null)
        {
            return this.NotFound();
        }

        ExternalSignInCookie.TenantFlow.Set(this.Response, challenge.ProtectedFlow, this.Request.IsHttps);
        return ExternalSignInRedirects.RedirectTo(this.Response, challengeRedirectUri);
    }

    /// <summary>
    ///     Completes tenant-scoped external sign-in and returns the shared application session payload. The callback
    ///     completes a sign-in only in the browser that holds the cookie of its challenge, and accepts each state once.
    ///     When an account with the email address of the identity exists and no link of the identity applies, the
    ///     callback signs nobody in: it keeps the identity as a pending link in an httpOnly cookie and returns to the
    ///     frontend with <c>link=pending</c> in the fragment.
    /// </summary>
    [EnableRateLimiting("auth")]
    [HttpGet("external/callback/{tenantSlug}")]
    [ProducesResponseType(typeof(TenantAuthSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteExternal(
        string tenantSlug,
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "code")] string? code,
        [FromQuery(Name = "error")] string? error,
        [FromQuery(Name = "error_description")]
        string? errorDescription,
        CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var protectedFlow = ExternalSignInCookie.TenantFlow.Read(this.Request);
        ExternalSignInCookie.TenantFlow.Clear(this.Response, this.Request.IsHttps);

        var completion = await tenantAuthService.CompleteExternalSignInAsync(
            tenantSlug,
            this.GetApplicationBaseUri(),
            new TenantExternalCallbackRequest(state, code, error, errorDescription),
            protectedFlow,
            ct);

        // The return page was checked when the sign-in started. A page whose origin no longer qualifies receives no
        // session and no pending link.
        Uri? returnUri = null;
        if (!string.IsNullOrEmpty(completion.FrontendReturnUrl))
        {
            returnUri = this.TryResolveReturnUrl(completion.FrontendReturnUrl);
            if (returnUri is null)
            {
                logger.LogWarning("TenantExternalSignInRejected Reason={Reason}", ReturnUrlNotAllowed);
                return this.Unauthorized(new { error = ReturnUrlNotAllowed, message = "The sign-in cannot return to the page it started from." });
            }
        }

        if (completion.ProtectedPendingLink is not null)
        {
            ExternalSignInCookie.PendingTenantLink.Set(this.Response, completion.ProtectedPendingLink, this.Request.IsHttps);
            return returnUri is null
                ? this.Unauthorized(new { error = completion.FailureCode, message = completion.FailureMessage })
                : ExternalSignInRedirects.RedirectTo(
                    this.Response,
                    ExternalSignInRedirects.WithFragment(returnUri, new Dictionary<string, string?> { ["link"] = "pending" }));
        }

        if (completion.User is null)
        {
            var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            var safeProviderError = (error ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderError={ProviderError}",
                safeTenantSlug,
                safeProviderError);

            var failureCode = completion.FailureCode ?? "external_identity_rejected";
            var failureMessage = completion.FailureMessage ?? "External identity rejected by tenant policy.";
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

        var session = await sessionFactory.CreateAsync(completion.User, ct);
        var sessionDto = AuthHelpers.ToTenantAuthSessionDto(session);
        // Refresh token goes only into the httpOnly cookie (set on this redirect response),
        // never the URL fragment, which would leak it via history/referrer.
        RefreshTokenCookie.Set(this.Response, session.RefreshToken, session.RefreshTokenExpiresAt, this.Request.IsHttps);
        return returnUri is null
            ? this.Ok(sessionDto)
            : ExternalSignInRedirects.RedirectTo(
                this.Response,
                ExternalSignInRedirects.WithFragment(
                    returnUri,
                    new Dictionary<string, string?>
                    {
                        ["accessToken"] = sessionDto.AccessToken,
                        ["expiresIn"] = sessionDto.ExpiresIn.ToString(CultureInfo.InvariantCulture),
                        ["tokenType"] = sessionDto.TokenType,
                    }));
    }

    /// <summary>
    ///     Returns the tenant sign-in that waits in this browser for the confirmation of its link to an account, or for a
    ///     new account.
    /// </summary>
    [HttpGet("external/pending-link")]
    [ProducesResponseType(typeof(TenantPendingLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetPendingLink(CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var pendingLink = await tenantAuthService.GetPendingLinkAsync(ExternalSignInCookie.PendingTenantLink.Read(this.Request), ct);
        return pendingLink is null
            ? this.NotFound(new { error = "pending_link_not_found", message = "No sign-in is waiting to be linked in this browser." })
            : this.Ok(pendingLink);
    }

    /// <summary>
    ///     Links the tenant sign-in that waits in this browser to the account that is signed in. The account receives a
    ///     membership in the tenant when it has none and the provider admits new members.
    /// </summary>
    [EnableRateLimiting("auth")]
    [HttpPost("external/pending-link/confirm")]
    [ProducesResponseType(typeof(TenantSignInLinkConfirmationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmPendingLink(CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null)
        {
            return this.Unauthorized(new { error = "authentication_required", message = "Sign in to the account that the sign-in is linked to." });
        }

        var outcome = await tenantAuthService.ConfirmPendingLinkAsync(
            ExternalSignInCookie.PendingTenantLink.Read(this.Request),
            userId.Value,
            ct);
        if (outcome.User is null)
        {
            return this.RefusePendingLink(outcome);
        }

        ExternalSignInCookie.PendingTenantLink.Clear(this.Response, this.Request.IsHttps);
        return this.Ok(new TenantSignInLinkConfirmationDto(outcome.TenantSlug!));
    }

    /// <summary>
    ///     Creates a new account for the tenant sign-in that waits in this browser and signs it in. The refresh token is
    ///     set as an httpOnly cookie.
    /// </summary>
    [EnableRateLimiting("auth")]
    [HttpPost("external/pending-link/new-account")]
    [ProducesResponseType(typeof(TenantExternalSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAccountForPendingLink(CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var outcome = await tenantAuthService.CreateAccountForPendingLinkAsync(
            ExternalSignInCookie.PendingTenantLink.Read(this.Request),
            ct);
        if (outcome.User is null)
        {
            return this.RefusePendingLink(outcome);
        }

        ExternalSignInCookie.PendingTenantLink.Clear(this.Response, this.Request.IsHttps);
        var session = await sessionFactory.CreateAsync(outcome.User, ct);
        RefreshTokenCookie.Set(this.Response, session.RefreshToken, session.RefreshTokenExpiresAt, this.Request.IsHttps);
        return this.Ok(new TenantExternalSessionDto(session.AccessToken, session.ExpiresIn, session.TokenType));
    }

    /// <summary>Returns the sign-ins of tenants that are linked to the account that is signed in.</summary>
    [HttpGet("external/links")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantSignInLinkDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ListSignInLinks(CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var userId = AuthHelpers.GetUserId(this.HttpContext);
        return userId is null
            ? this.Unauthorized(new { error = "authentication_required", message = "Valid credentials required." })
            : this.Ok(await tenantAuthService.ListSignInLinksAsync(userId.Value, ct));
    }

    private static bool TryParseMode(string? mode, out TenantExternalSignInMode signInMode)
    {
        signInMode = TenantExternalSignInMode.SignIn;
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "signIn", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(mode, "link", StringComparison.Ordinal))
        {
            signInMode = TenantExternalSignInMode.Link;
            return true;
        }

        return false;
    }

    private IActionResult RefusePendingLink(TenantPendingLinkOutcome outcome)
    {
        var body = new { error = outcome.FailureCode, message = outcome.FailureMessage };
        switch (outcome.FailureCode)
        {
            case "pending_link_not_found":
                // The cookie holds nothing usable any more, so the browser drops it.
                ExternalSignInCookie.PendingTenantLink.Clear(this.Response, this.Request.IsHttps);
                return this.NotFound(body);
            case "tenant_or_provider_inactive":
                return this.NotFound(body);
            case "identity_already_linked":
                return this.Conflict(body);
            default:
                return this.StatusCode(StatusCodes.Status403Forbidden, body);
        }
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

/// <summary>Tenant-local login request payload.</summary>
public sealed record TenantLocalLoginRequest(string Username, string Password)
{
    /// <summary>Renders the request without the password; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(TenantLocalLoginRequest)} {{ Username = {this.Username}, "
               + $"Password = {SecretSafeRendering.Elide(this.Password)} }}";
    }
}

/// <summary>The tenant whose sign-in was linked to the account that is signed in.</summary>
/// <param name="TenantSlug">The slug of the tenant of the provider.</param>
public sealed record TenantSignInLinkConfirmationDto(string TenantSlug);

/// <summary>The access token of a session that a tenant sign-in started. The refresh token is set as an httpOnly cookie.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="ExpiresIn">The seconds until the access token expires.</param>
/// <param name="TokenType">The type of the access token, <c>Bearer</c>.</param>
public sealed record TenantExternalSessionDto(string AccessToken, int ExpiresIn, string TokenType)
{
    /// <summary>Renders the session without the token; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(TenantExternalSessionDto)} {{ AccessToken = {SecretSafeRendering.Elide(this.AccessToken)}, "
               + $"ExpiresIn = {this.ExpiresIn}, TokenType = {this.TokenType} }}";
    }
}
