// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The sign-in of platform administrators through the OpenID Connect identity provider configured for the
///     installation. At every sign-in, the role in the ID token decides whether the account holds platform
///     administrator rights.
/// </summary>
public interface IPlatformAdminSignInService
{
    /// <summary>Builds the redirect to the provider and the protected state of the sign-in.</summary>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="frontendReturnUrl">The frontend page the sign-in returns to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The challenge, or <see langword="null" /> when the provider's discovery document cannot be used.</returns>
    Task<PlatformAdminSignInChallenge?> BuildChallengeAsync(
        Uri applicationBaseUri,
        Uri frontendReturnUrl,
        CancellationToken ct = default);

    /// <summary>
    ///     Completes the sign-in from the provider's callback: checks the state, redeems the code, validates the ID
    ///     token, and applies the role to the account.
    /// </summary>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="callback">The query parameters of the callback.</param>
    /// <param name="protectedFlow">The protected state that the challenge stored in the browser.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The signed-in account, or the reason the sign-in was refused.</returns>
    Task<PlatformAdminSignInCompletion> CompleteSignInAsync(
        Uri applicationBaseUri,
        PlatformAdminSignInCallback callback,
        string? protectedFlow,
        CancellationToken ct = default);
}

/// <summary>The redirect that starts a sign-in through the identity provider for platform administrators.</summary>
/// <param name="RedirectUrl">The authorization request the browser is sent to.</param>
/// <param name="AllowedRedirectOrigin">The origin of the provider's authorization endpoint.</param>
/// <param name="ProtectedFlow">
///     The protected state of the sign-in, which the browser keeps in a cookie until the callback.
/// </param>
public sealed record PlatformAdminSignInChallenge(string RedirectUrl, string AllowedRedirectOrigin, string ProtectedFlow)
{
    /// <summary>Renders the challenge without its protected state; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(PlatformAdminSignInChallenge)} {{ AllowedRedirectOrigin = {this.AllowedRedirectOrigin}, "
               + $"ProtectedFlow = {SecretSafeRendering.Elide(this.ProtectedFlow)} }}";
    }
}

/// <summary>The query parameters of the provider's callback.</summary>
/// <param name="State">The state the authorization request sent.</param>
/// <param name="Code">The authorization code.</param>
/// <param name="Error">The error code of a refused sign-in.</param>
/// <param name="ErrorDescription">The provider's description of the error.</param>
public sealed record PlatformAdminSignInCallback(string? State, string? Code, string? Error, string? ErrorDescription)
{
    /// <summary>Renders the callback without the authorization code; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(PlatformAdminSignInCallback)} {{ Code = {SecretSafeRendering.Elide(this.Code)}, "
               + $"Error = {this.Error} }}";
    }
}

/// <summary>The outcome of a sign-in through the identity provider for platform administrators.</summary>
/// <param name="User">The signed-in account, or <see langword="null" /> when the sign-in was refused.</param>
/// <param name="FrontendReturnUrl">The frontend page the sign-in returns to, when it is known.</param>
/// <param name="FailureCode">A stable code for the refusal.</param>
/// <param name="FailureMessage">What the person signing in reads about the refusal.</param>
public sealed record PlatformAdminSignInCompletion(
    AppUser? User,
    string? FrontendReturnUrl,
    string? FailureCode = null,
    string? FailureMessage = null);
