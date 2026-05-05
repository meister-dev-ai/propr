// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Tenant-scoped login discovery and sign-in endpoints.</summary>
[ApiController]
public sealed class TenantAuthController(
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
    [HttpGet("/auth/tenants/{tenantSlug}/providers")]
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
    [HttpPost("/auth/tenants/{tenantSlug}/local-login")]
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
        return this.Ok(AuthHelpers.ToTenantAuthSessionDto(session));
    }

    /// <summary>Starts the tenant-scoped external sign-in flow for the selected provider.</summary>
    [HttpGet("/auth/external/challenge/{tenantSlug}/{providerId:guid}")]
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

        var frontendReturnUrl = this.TryResolveFrontendReturnUrl(returnUrl);

        var challenge = await tenantAuthService.BuildExternalChallengeAsync(
            tenantSlug,
            providerId,
            this.GetApplicationBaseUri(),
            frontendReturnUrl,
            ct);
        return challenge is null ? this.NotFound() : this.Redirect(challenge.RedirectUrl);
    }

    /// <summary>Completes tenant-scoped external sign-in and returns the shared application session payload.</summary>
    [HttpGet("/auth/external/callback/{tenantSlug}")]
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
            var failedRedirectUrl = this.TryBuildFrontendCallbackRedirectUrl(
                completion.FrontendReturnUrl,
                new Dictionary<string, string?>
                {
                    ["error"] = failureCode,
                    ["message"] = failureMessage,
                });

            return failedRedirectUrl is not null
                ? this.Redirect(failedRedirectUrl)
                : this.Unauthorized(new { error = failureCode, message = failureMessage });
        }

        var session = await sessionFactory.CreateAsync(completion.User, ct);
        var sessionDto = AuthHelpers.ToTenantAuthSessionDto(session);
        var successfulRedirectUrl = this.TryBuildFrontendCallbackRedirectUrl(
            completion.FrontendReturnUrl,
            new Dictionary<string, string?>
            {
                ["accessToken"] = sessionDto.AccessToken,
                ["refreshToken"] = sessionDto.RefreshToken,
                ["expiresIn"] = sessionDto.ExpiresIn.ToString(CultureInfo.InvariantCulture),
                ["tokenType"] = sessionDto.TokenType,
            });

        return successfulRedirectUrl is not null
            ? this.Redirect(successfulRedirectUrl)
            : this.Ok(sessionDto);
    }

    private Uri GetApplicationBaseUri()
    {
        return PublicApplicationUrlResolver.GetApplicationBaseUri(this.Request, configuration);
    }

    private Uri? TryResolveFrontendReturnUrl(string? returnUrl)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri))
        {
            return null;
        }

        if (!string.Equals(returnUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(returnUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var origin = returnUri.GetLeftPart(UriPartial.Authority);
        return BrowserOriginPolicy.IsAllowedOrigin(origin, configuration)
            ? returnUri
            : null;
    }

    private string? TryBuildFrontendCallbackRedirectUrl(
        string? frontendReturnUrl,
        IReadOnlyDictionary<string, string?> fragmentValues)
    {
        if (!Uri.TryCreate(frontendReturnUrl, UriKind.Absolute, out var returnUri))
        {
            return null;
        }

        if (!string.Equals(returnUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(returnUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var origin = returnUri.GetLeftPart(UriPartial.Authority);
        if (!BrowserOriginPolicy.IsAllowedOrigin(origin, configuration))
        {
            return null;
        }

        var fragment = string.Join(
            "&",
            fragmentValues
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value!)}"));

        var builder = new UriBuilder(returnUri)
        {
            Fragment = fragment,
        };

        return builder.Uri.ToString();
    }
}

/// <summary>Tenant-local login request payload.</summary>
public sealed record TenantLocalLoginRequest(string Username, string Password);
