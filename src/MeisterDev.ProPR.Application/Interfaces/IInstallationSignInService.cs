// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     A sign-in through an OpenID Connect identity provider that the installation configures: the redirect to the
///     provider, and the callback that signs in the account of the identity at the provider.
/// </summary>
public interface IInstallationSignInService
{
    /// <summary>What the person signing in reads when the provider cannot be reached.</summary>
    string ProviderUnavailableMessage { get; }

    /// <summary>Builds the redirect to the provider and the protected state of the sign-in.</summary>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="frontendReturnUrl">The frontend page the sign-in returns to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The challenge, or <see langword="null" /> when the provider's discovery document cannot be used.</returns>
    Task<InstallationSignInChallenge?> BuildChallengeAsync(
        Uri applicationBaseUri,
        Uri frontendReturnUrl,
        CancellationToken ct = default);

    /// <summary>
    ///     Completes the sign-in from the provider's callback: checks the state, redeems the code, validates the ID
    ///     token, and finds the account of the identity.
    /// </summary>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="callback">The query parameters of the callback.</param>
    /// <param name="protectedFlow">The protected state that the challenge stored in the browser.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The signed-in account, or the reason the sign-in was refused.</returns>
    Task<InstallationSignInCompletion> CompleteSignInAsync(
        Uri applicationBaseUri,
        InstallationSignInCallback callback,
        string? protectedFlow,
        CancellationToken ct = default);
}

/// <summary>The redirect that starts a sign-in through an identity provider of the installation.</summary>
/// <param name="RedirectUrl">The authorization request the browser is sent to.</param>
/// <param name="AllowedRedirectOrigin">The origin of the provider's authorization endpoint.</param>
/// <param name="ProtectedFlow">
///     The protected state of the sign-in, which the browser keeps in a cookie until the callback.
/// </param>
public sealed record InstallationSignInChallenge(string RedirectUrl, string AllowedRedirectOrigin, string ProtectedFlow)
{
    /// <summary>Renders the challenge without its protected state; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(InstallationSignInChallenge)} {{ AllowedRedirectOrigin = {this.AllowedRedirectOrigin}, "
               + $"ProtectedFlow = {SecretSafeRendering.Elide(this.ProtectedFlow)} }}";
    }
}

/// <summary>The query parameters of the provider's callback.</summary>
/// <param name="State">The state the authorization request sent.</param>
/// <param name="Code">The authorization code.</param>
/// <param name="Error">The error code of a refused sign-in.</param>
/// <param name="ErrorDescription">The provider's description of the error.</param>
public sealed record InstallationSignInCallback(string? State, string? Code, string? Error, string? ErrorDescription)
{
    /// <summary>Renders the callback without the authorization code; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(InstallationSignInCallback)} {{ Code = {SecretSafeRendering.Elide(this.Code)}, "
               + $"Error = {this.Error} }}";
    }
}

/// <summary>The outcome of a sign-in through an identity provider of the installation.</summary>
/// <param name="User">The signed-in account, or <see langword="null" /> when the sign-in was refused.</param>
/// <param name="FrontendReturnUrl">The frontend page the sign-in returns to, when it is known.</param>
/// <param name="FailureCode">A stable code for the refusal.</param>
/// <param name="FailureMessage">What the person signing in reads about the refusal.</param>
public sealed record InstallationSignInCompletion(
    AppUser? User,
    string? FrontendReturnUrl,
    string? FailureCode = null,
    string? FailureMessage = null);
