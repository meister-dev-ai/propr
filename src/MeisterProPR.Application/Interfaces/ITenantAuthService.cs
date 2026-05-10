// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Tenant-aware sign-in orchestration for login-option discovery and tenant-scoped authentication.</summary>
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
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The challenge metadata when the provider is valid; otherwise <c>null</c>.</returns>
    Task<TenantExternalChallengeResult?> BuildExternalChallengeAsync(
        string tenantSlug,
        Guid providerId,
        Uri applicationBaseUri,
        Uri? frontendReturnUrl = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Completes an external tenant sign-in flow from the provider callback payload.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="applicationBaseUri">Public application base URI used for callbacks.</param>
    /// <param name="callback">Provider callback payload to validate.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The completion result describing the sign-in outcome.</returns>
    Task<TenantExternalSignInCompletionResult> CompleteExternalSignInAsync(
        string tenantSlug,
        Uri applicationBaseUri,
        TenantExternalCallbackRequest callback,
        CancellationToken ct = default);
}

/// <summary>Redirect metadata for starting an external tenant sign-in flow.</summary>
public sealed record TenantExternalChallengeResult(
    string RedirectUrl,
    string CallbackUrl,
    string State,
    string AllowedRedirectOrigin);

/// <summary>Provider callback query parameters that must be validated server-side.</summary>
public sealed record TenantExternalCallbackRequest(
    string? State,
    string? Code,
    string? Error,
    string? ErrorDescription);

/// <summary>Outcome of a tenant external sign-in callback, including optional SPA handoff metadata.</summary>
public sealed record TenantExternalSignInCompletionResult(
    AppUser? User,
    string? FrontendReturnUrl,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>External identity information presented back to the tenant auth callback.</summary>
public sealed record TenantExternalIdentityPayload(
    string Issuer,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? DisplayName);
