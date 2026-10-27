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
    IEnumerable<ScmBrowserOriginDeclaration> browserOriginDeclarations,
    ITenantAuthService tenantAuthService,
    ISessionFactory sessionFactory,
    ILogger<TenantAuthController> logger,
    IConfiguration configuration,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
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

    /// <summary>Starts the tenant-scoped external sign-in flow for the selected provider.</summary>
    [HttpGet("external/challenge/{tenantSlug}/{providerId:guid}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChallengeExternal(
        string tenantSlug,
        Guid providerId,
        [FromQuery(Name = "returnUrl")] string? returnUrl,
        CancellationToken ct)
    {
        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var frontendReturnUrl = ExternalSignInRedirects.TryResolveFrontendReturnUrl(
            returnUrl,
            configuration,
            browserOriginDeclarations.ToArray());

        var challenge = await tenantAuthService.BuildExternalChallengeAsync(
            tenantSlug,
            providerId,
            this.GetApplicationBaseUri(),
            frontendReturnUrl,
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

        return ExternalSignInRedirects.RedirectTo(this.Response, challengeRedirectUri);
    }

    /// <summary>Completes tenant-scoped external sign-in and returns the shared application session payload.</summary>
    [HttpGet("external/callback/{tenantSlug}")]
    [ProducesResponseType(typeof(TenantAuthSessionDto), StatusCodes.Status200OK)]
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

        var completion = await tenantAuthService.CompleteExternalSignInAsync(
            tenantSlug,
            this.GetApplicationBaseUri(),
            new TenantExternalCallbackRequest(state, code, error, errorDescription),
            ct);

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
            var failedRedirectUri = ExternalSignInRedirects.TryBuildFrontendCallbackUri(
                completion.FrontendReturnUrl,
                new Dictionary<string, string?>
                {
                    ["error"] = failureCode,
                    ["message"] = failureMessage,
                },
                configuration,
                browserOriginDeclarations.ToArray());
            if (failedRedirectUri is not null)
            {
                return ExternalSignInRedirects.RedirectTo(this.Response, failedRedirectUri);
            }

            return this.Unauthorized(new { error = failureCode, message = failureMessage });
        }

        var session = await sessionFactory.CreateAsync(completion.User, ct);
        var sessionDto = AuthHelpers.ToTenantAuthSessionDto(session);
        // Refresh token goes only into the httpOnly cookie (set on this redirect response),
        // never the URL fragment, which would leak it via history/referrer.
        RefreshTokenCookie.Set(this.Response, session.RefreshToken, session.RefreshTokenExpiresAt, this.Request.IsHttps);
        var successfulRedirectUri = ExternalSignInRedirects.TryBuildFrontendCallbackUri(
            completion.FrontendReturnUrl,
            new Dictionary<string, string?>
            {
                ["accessToken"] = sessionDto.AccessToken,
                ["expiresIn"] = sessionDto.ExpiresIn.ToString(CultureInfo.InvariantCulture),
                ["tokenType"] = sessionDto.TokenType,
            },
            configuration,
            browserOriginDeclarations.ToArray());
        if (successfulRedirectUri is not null)
        {
            return ExternalSignInRedirects.RedirectTo(this.Response, successfulRedirectUri);
        }

        return this.Ok(sessionDto);
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
