// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     The challenge and the callback of a sign-in through an OpenID Connect identity provider that the installation
///     configures. A derived controller names the routes, the provider and the sign-in service.
/// </summary>
/// <remarks>
///     A successful callback sets the refresh cookie and returns the browser to the frontend with the access token in the
///     URL fragment. The return page must be on an origin that serves the user interface of the installation, which
///     ProPR checks when the sign-in starts and again at the callback.
/// </remarks>
/// <param name="sessionFactory">Issues the session of a signed-in account.</param>
/// <param name="configuration">The configuration that names the public addresses of the installation.</param>
/// <param name="environment">The host environment, which decides whether the local frontend origins qualify.</param>
/// <param name="logger">The logger of the derived controller.</param>
/// <param name="licensingCapabilityService">The licensing service, or <see langword="null" /> in a host without one.</param>
public abstract class InstallationSignInController(
    ISessionFactory sessionFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger logger,
    ILicensingCapabilityService? licensingCapabilityService) : ControllerBase
{
    /// <summary>Whether the installation configured the provider.</summary>
    protected abstract bool IsProviderConfigured { get; }

    /// <summary>The sign-in service, or <see langword="null" /> in a host without a database.</summary>
    protected abstract IInstallationSignInService? SignInService { get; }

    /// <summary>The cookie that keeps the state of a sign-in of this kind until the callback.</summary>
    protected abstract ExternalSignInCookie FlowCookie { get; }

    /// <summary>Runs after the callback signed an account in, before the browser returns to the frontend.</summary>
    /// <param name="user">The signed-in account.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    protected virtual Task OnSignedInAsync(AppUser user, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Starts the sign-in. ProPR sends the browser to the identity provider and keeps the state of the sign-in in an
    ///     httpOnly cookie.
    /// </summary>
    /// <param name="returnUrl">The frontend page the sign-in returns to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    protected async Task<IActionResult> ChallengeAsync(string? returnUrl, CancellationToken ct)
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

        var signInService = this.SignInService!;
        var challenge = await signInService.BuildChallengeAsync(this.GetApplicationBaseUri(), frontendReturnUrl, ct);
        var providerRedirectUri = challenge is null
            ? null
            : ExternalSignInRedirects.TryResolveProviderRedirectUri(challenge.RedirectUrl, challenge.AllowedRedirectOrigin);
        if (challenge is null || providerRedirectUri is null)
        {
            logger.LogWarning("InstallationSignInRefused Reason={Reason}", "provider_unavailable");
            return this.RedirectWithFailure(
                frontendReturnUrl.ToString(),
                "provider_unavailable",
                signInService.ProviderUnavailableMessage);
        }

        this.FlowCookie.Set(this.Response, challenge.ProtectedFlow, this.Request.IsHttps);
        return ExternalSignInRedirects.RedirectTo(this.Response, providerRedirectUri);
    }

    /// <summary>
    ///     Completes the sign-in at the redirect from the identity provider. On success it sets the refresh cookie and
    ///     returns the browser to the frontend with the access token in the URL fragment. A refused sign-in returns to
    ///     the frontend with <c>error</c> and <c>message</c> in the fragment.
    /// </summary>
    /// <param name="state">The state of the authorization request.</param>
    /// <param name="code">The authorization code.</param>
    /// <param name="error">The error code of a sign-in the provider refused.</param>
    /// <param name="errorDescription">The provider's description of the error.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    protected async Task<IActionResult> CallbackAsync(
        string? state,
        string? code,
        string? error,
        string? errorDescription,
        CancellationToken ct)
    {
        if (await this.RefuseUnavailableProviderAsync(ct) is { } refusal)
        {
            return refusal;
        }

        var protectedFlow = this.FlowCookie.Read(this.Request);
        this.FlowCookie.Clear(this.Response, this.Request.IsHttps);

        var completion = await this.SignInService!.CompleteSignInAsync(
            this.GetApplicationBaseUri(),
            new InstallationSignInCallback(state, code, error, errorDescription),
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
        await this.OnSignedInAsync(completion.User, ct);
        return ExternalSignInRedirects.RedirectTo(this.Response, successfulRedirectUri);
    }

    /// <summary>
    ///     Answers for a provider that is not in use: <c>404</c> when the installation configured none or runs without a
    ///     database, <c>409</c> when the license does not grant single sign-on.
    /// </summary>
    private async Task<IActionResult?> RefuseUnavailableProviderAsync(CancellationToken ct)
    {
        if (!this.IsProviderConfigured || this.SignInService is null || licensingCapabilityService is null)
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
