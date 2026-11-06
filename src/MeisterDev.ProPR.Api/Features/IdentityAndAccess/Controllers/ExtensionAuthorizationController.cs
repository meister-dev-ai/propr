// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>ProPR website-session authorization for the registered browser extension.</summary>
[ApiController]
[Route("auth/extension")]
[TenantData(TenantDataAccess.None)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ExtensionAuthorizationController(
    ExtensionAuthorizationOptions options,
    IConfiguration configuration,
    IExtensionAuthorizationService? service = null,
    IHostEnvironment? environment = null) : ControllerBase
{
    /// <summary>Returns the available versioned extension sign-in protocol and trusted endpoints.</summary>
    /// <response code="200">Configured metadata, or an explicit disabled reason.</response>
    [AllowAnonymous]
    [HttpGet("options")]
    [ProducesResponseType(typeof(ExtensionSignInOptionsDto), StatusCodes.Status200OK)]
    public ActionResult<ExtensionSignInOptionsDto> GetOptions()
    {
        if (service is not null)
        {
            return service.GetOptions();
        }

        // Without a database, sign-in is unavailable: not_configured when the installation sets no extension sign-in,
        // and database_required when it sets one.
        var reason = options.UnavailableReason ?? (options.Enabled ? "database_required" : "not_configured");
        return new ExtensionSignInOptionsDto(1, false, reason, options.Issuer, null, null, ["S256"]);
    }

    /// <summary>Starts an interactive or silent authorization-code flow using the website session.</summary>
    /// <param name="clientId">Registered public client identifier.</param>
    /// <param name="redirectUri">Exactly registered browser callback.</param>
    /// <param name="responseType">Must be code.</param>
    /// <param name="codeChallenge">S256 challenge.</param>
    /// <param name="codeChallengeMethod">Must be S256.</param>
    /// <param name="state">Opaque state returned unchanged.</param>
    /// <param name="prompt">Optional none for noninteractive renewal.</param>
    /// <param name="uiOrigin">Optional canonical HTTPS frontend origin trusted by this installation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="302">Browser callback or the ProPR login/consent surface.</response>
    /// <response code="400">Untrusted or invalid authorization request.</response>
    /// <response code="503">Extension sign-in is unavailable.</response>
    [AllowAnonymous]
    [EnableRateLimiting("extension-sign-in")]
    [HttpGet("authorize")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ExtensionAuthFailureDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ExtensionAuthFailureDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Authorize(
        [FromQuery(Name = "client_id")] string clientId,
        [FromQuery(Name = "redirect_uri")] string redirectUri, [FromQuery(Name = "response_type")] string responseType,
        [FromQuery(Name = "code_challenge")] string codeChallenge, [FromQuery(Name = "code_challenge_method")] string codeChallengeMethod,
        [FromQuery] string state, [FromQuery] string? prompt = null, [FromQuery(Name = "ui_origin")] string? uiOrigin = null,
        CancellationToken ct = default)
    {
        this.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (service is null || !options.Enabled)
        {
            return this.StatusCode(503, new ExtensionAuthFailureDto("sign_in_unavailable"));
        }

        // Behind a proxy that terminates TLS, the request counts as HTTPS when a trusted proxy reports https in
        // X-Forwarded-Proto. ProPR answers with an error code of its own, so that an operator who calls this endpoint
        // can see that the request did not reach ProPR over HTTPS.
        if (!this.Request.IsHttps)
        {
            return this.StatusCode(503, new ExtensionAuthFailureDto("https_required"));
        }

        var hasUiOrigin = uiOrigin is not null || this.Request.Query.ContainsKey("ui_origin");
        if (hasUiOrigin && !this.IsTrustedUiOrigin(uiOrigin))
        {
            return this.BadRequest(new ExtensionAuthFailureDto("invalid_ui_origin"));
        }

        var origin = hasUiOrigin
            ? uiOrigin!
            : PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration) ?? new Uri(options.Issuer!).GetLeftPart(UriPartial.Authority);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var ui) || ui.Scheme != Uri.UriSchemeHttps)
        {
            return this.StatusCode(503, new ExtensionAuthFailureDto("insecure_ui_origin"));
        }

        var result = await service.StartAsync(
            new(clientId, redirectUri, responseType, codeChallenge, codeChallengeMethod, state, prompt), RefreshTokenCookie.Read(this.Request), ct);
        if (result.RedirectUrl is not null)
        {
            return ExternalSignInRedirects.RedirectTo(this.Response, new Uri(result.RedirectUrl));
        }

        if (result.PendingRequest is null)
        {
            return this.BadRequest(new ExtensionAuthFailureDto(result.Error ?? "invalid_request"));
        }

        return ExternalSignInRedirects.RedirectTo(
            this.Response, new Uri(origin + "/extension/authorize?request=" + Uri.EscapeDataString(result.PendingRequest)));
    }

    private bool IsTrustedUiOrigin(string? origin)
    {
        if (origin is null || origin.Length > 2048 || this.Request.Query["ui_origin"].Count > 1
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || origin != uri.GetLeftPart(UriPartial.Authority))
        {
            return false;
        }

        return ExternalSignInRedirects.TryResolveUserInterfaceReturnUrl(origin, this.Request, configuration, environment?.IsDevelopment() == true) is not null;
    }

    /// <summary>Returns consent details for the current authenticated website user and session.</summary>
    /// <param name="request">Protected pending request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Application and permission details.</response>
    /// <response code="401">Website authentication is required.</response>
    /// <response code="400">The pending request expired or does not match the website session.</response>
    [HttpGet("consent")]
    [ProducesResponseType(typeof(ExtensionConsentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ExtensionAuthFailureDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetConsent([FromQuery] string request, CancellationToken ct)
    {
        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null || !this.User.HasClaim("aud", "meisterpropr"))
        {
            return this.Unauthorized();
        }

        var consent = service is null ? null : await service.GetConsentAsync(request, userId.Value, RefreshTokenCookie.Read(this.Request), ct);
        return consent is null ? this.BadRequest(new ExtensionAuthFailureDto("invalid_request")) : this.Ok(consent);
    }

    /// <summary>Records a user's explicit application consent or denial.</summary>
    /// <param name="request">Protected pending request and decision.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Validated callback URL carrying the code or denial.</response>
    /// <response code="401">Website authentication is required.</response>
    /// <response code="400">The pending request expired or was consumed.</response>
    [HttpPost("consent")]
    [ProducesResponseType(typeof(ExtensionAuthorizationRedirectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ExtensionAuthFailureDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Decide([FromBody] ExtensionDecisionRequest request, CancellationToken ct)
    {
        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null || !this.User.HasClaim("aud", "meisterpropr"))
        {
            return this.Unauthorized();
        }

        var result = service is null
            ? null
            : await service.DecideAsync(request.Request, userId.Value, RefreshTokenCookie.Read(this.Request), request.Approve, ct);
        return result?.RedirectUrl is null
            ? this.BadRequest(new ExtensionAuthFailureDto("invalid_request"))
            : this.Ok(new ExtensionAuthorizationRedirectDto(result.RedirectUrl));
    }

    /// <summary>Exchanges one PKCE-bound authorization code for short-lived extension access.</summary>
    /// <param name="request">Code and its original verifier, client and callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Access token, lifetime, subject and issuer; no refresh token.</response>
    /// <response code="400">Invalid, expired or already redeemed code.</response>
    [AllowAnonymous]
    [EnableRateLimiting("extension-sign-in")]
    [HttpPost("token")]
    [ProducesResponseType(typeof(ExtensionTokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ExtensionAuthFailureDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Exchange([FromBody] ExtensionTokenRequest request, CancellationToken ct)
    {
        var token = service is null || !this.Request.IsHttps ? null : await service.ExchangeAsync(request, ct);
        return token is null ? this.BadRequest(new ExtensionAuthFailureDto("invalid_grant")) : this.Ok(token);
    }

    /// <summary>Lists the current user's active application consents.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The current user's grants.</response>
    /// <response code="401">Website authentication is required.</response>
    [HttpGet("grants")]
    [ProducesResponseType(typeof(IReadOnlyList<ExtensionGrantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListGrants(CancellationToken ct)
    {
        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null || !this.User.HasClaim("aud", "meisterpropr"))
        {
            return this.Unauthorized();
        }

        return this.Ok(service is null ? Array.Empty<ExtensionGrantDto>() : await service.ListGrantsAsync(userId.Value, ct));
    }

    /// <summary>Revokes one application consent belonging to the current user.</summary>
    /// <param name="id">Consent grant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The user's grant is revoked or absent.</response>
    /// <response code="401">Website authentication is required.</response>
    [HttpDelete("grants/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var userId = AuthHelpers.GetUserId(this.HttpContext);
        if (userId is null || !this.User.HasClaim("aud", "meisterpropr"))
        {
            return this.Unauthorized();
        }

        if (service is not null)
        {
            await service.RevokeAsync(userId.Value, id, ct);
        }

        return this.NoContent();
    }
}

public sealed record ExtensionAuthFailureDto(string Error);

public sealed record ExtensionAuthorizationRedirectDto(string RedirectUrl)
{
    public override string ToString() => "ExtensionAuthorizationRedirectDto { redacted }";
}
