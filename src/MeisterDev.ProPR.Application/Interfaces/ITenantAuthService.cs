// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Tenant-aware sign-in orchestration for login-option discovery and tenant-scoped authentication.</summary>
/// <remarks>
///     An external identity reaches an account only through a link of that identity. The email address that an
///     identity provider states never selects an existing account. When an account with that address exists and no
///     link applies, the sign-in keeps the identity as a pending link: the person confirms the link from the account
///     they sign in to, or creates a new account for the identity.
/// </remarks>
public interface ITenantAuthService
{
    /// <summary>
    ///     Resolves tenant-specific login options by tenant slug.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The login options when the tenant exists; otherwise <c>null</c>.</returns>
    Task<TenantLoginOptionsDto?> GetLoginOptionsAsync(string tenantSlug, CancellationToken ct = default);

    /// <summary>
    ///     Authenticates a user through the tenant's local-login flow.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="username">Supplied username.</param>
    /// <param name="password">Supplied password.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The authenticated user when credentials are valid; otherwise <c>null</c>.</returns>
    Task<AppUser?> AuthenticateLocalAsync(string tenantSlug, string username, string password, CancellationToken ct = default);

    /// <summary>
    ///     Builds the redirect metadata required to start an external tenant sign-in flow.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="providerId">External sign-in provider identifier.</param>
    /// <param name="applicationBaseUri">Public application base URI used for callbacks.</param>
    /// <param name="frontendReturnUrl">Optional frontend return URL.</param>
    /// <param name="mode">Whether the sign-in signs a person in or links the identity to the account that is signed in.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The challenge metadata when the provider is valid; otherwise <c>null</c>.</returns>
    Task<TenantExternalChallengeResult?> BuildExternalChallengeAsync(
        string tenantSlug,
        Guid providerId,
        Uri applicationBaseUri,
        Uri? frontendReturnUrl = null,
        TenantExternalSignInMode mode = TenantExternalSignInMode.SignIn,
        CancellationToken ct = default);

    /// <summary>
    ///     Completes an external tenant sign-in flow from the provider callback payload.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="applicationBaseUri">Public application base URI used for callbacks.</param>
    /// <param name="callback">Provider callback payload to validate.</param>
    /// <param name="protectedFlow">
    ///     The protected state of the sign-in that the challenge handed to the browser in a cookie, or null when the
    ///     browser sent none.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The completion result describing the sign-in outcome.</returns>
    Task<TenantExternalSignInCompletionResult> CompleteExternalSignInAsync(
        string tenantSlug,
        Uri applicationBaseUri,
        TenantExternalCallbackRequest callback,
        string? protectedFlow,
        CancellationToken ct = default);

    /// <summary>Returns the pending link that the protected value holds, or null when it is invalid, expired or used.</summary>
    /// <param name="protectedPendingLink">The protected pending link that a callback issued.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<TenantPendingLinkDto?> GetPendingLinkAsync(string? protectedPendingLink, CancellationToken ct = default);

    /// <summary>
    ///     Links the identity of a pending link to the account <paramref name="userId" />, the account that is signed in
    ///     when the person confirms the link. The account receives a membership in the tenant of the provider when it has
    ///     none and the provider admits new members.
    /// </summary>
    /// <param name="protectedPendingLink">The protected pending link that a callback issued.</param>
    /// <param name="userId">The account that is signed in.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<TenantPendingLinkOutcome> ConfirmPendingLinkAsync(
        string? protectedPendingLink,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>Creates a new account for the identity of a pending link, with a membership in the tenant of the provider.</summary>
    /// <param name="protectedPendingLink">The protected pending link that a callback issued.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<TenantPendingLinkOutcome> CreateAccountForPendingLinkAsync(
        string? protectedPendingLink,
        CancellationToken ct = default);

    /// <summary>Returns the sign-ins of tenants that are linked to the account <paramref name="userId" />.</summary>
    /// <param name="userId">The account that is signed in.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IReadOnlyList<TenantSignInLinkDto>> ListSignInLinksAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>What an external tenant sign-in does with the identity that the provider confirms.</summary>
public enum TenantExternalSignInMode
{
    /// <summary>The sign-in signs the person in to the account of the identity.</summary>
    SignIn = 0,

    /// <summary>
    ///     The sign-in signs nobody in. It keeps the identity as a pending link for the account that is signed in when
    ///     the person confirms the link.
    /// </summary>
    Link = 1,
}

/// <summary>Redirect metadata for starting an external tenant sign-in flow.</summary>
/// <param name="RedirectUrl">The authorization request the browser is sent to.</param>
/// <param name="AllowedRedirectOrigin">The origin of the authorization endpoint of the provider.</param>
/// <param name="ProtectedFlow">
///     The protected state, nonce and PKCE code verifier of the sign-in, which the browser keeps in an httpOnly cookie
///     until the callback.
/// </param>
public sealed record TenantExternalChallengeResult(
    string RedirectUrl,
    string AllowedRedirectOrigin,
    string ProtectedFlow);

/// <summary>Provider callback query parameters that must be validated server-side.</summary>
public sealed record TenantExternalCallbackRequest(
    string? State,
    string? Code,
    string? Error,
    string? ErrorDescription);

/// <summary>Outcome of a tenant external sign-in callback, including optional SPA handoff metadata.</summary>
/// <param name="User">The account that the sign-in signed in, or null.</param>
/// <param name="FrontendReturnUrl">The frontend page the sign-in returns to.</param>
/// <param name="FailureCode">The stable code of a sign-in that signed nobody in.</param>
/// <param name="FailureMessage">The text the person reads about that outcome.</param>
/// <param name="ProtectedPendingLink">
///     The protected pending link, when the identity waits for the confirmation of its link or for a new account.
/// </param>
public sealed record TenantExternalSignInCompletionResult(
    AppUser? User,
    string? FrontendReturnUrl,
    string? FailureCode = null,
    string? FailureMessage = null,
    string? ProtectedPendingLink = null);

/// <summary>The outcome of using a pending link: the account that now holds the identity, or why ProPR refused.</summary>
/// <param name="User">The account that the identity is linked to, or null when ProPR refused.</param>
/// <param name="TenantSlug">The tenant of the provider of the identity.</param>
/// <param name="FailureCode">The stable code of a refusal.</param>
/// <param name="FailureMessage">The text the person reads about a refusal.</param>
public sealed record TenantPendingLinkOutcome(
    AppUser? User,
    string? TenantSlug,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>External identity information presented back to the tenant auth callback.</summary>
public sealed record TenantExternalIdentityPayload(
    string Issuer,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? DisplayName);
