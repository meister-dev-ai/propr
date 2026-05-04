// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Tenant-aware sign-in orchestration for login-option discovery and tenant-scoped authentication.</summary>
public interface ITenantAuthService
{
    Task<TenantLoginOptionsDto?> GetLoginOptionsAsync(string tenantSlug, CancellationToken ct = default);

    Task<AppUser?> AuthenticateLocalAsync(string tenantSlug, string username, string password, CancellationToken ct = default);

    Task<TenantExternalChallengeResult?> BuildExternalChallengeAsync(
        string tenantSlug,
        Guid providerId,
        Uri applicationBaseUri,
        Uri? frontendReturnUrl = null,
        CancellationToken ct = default);

    Task<TenantExternalSignInCompletionResult> CompleteExternalSignInAsync(
        string tenantSlug,
        Uri applicationBaseUri,
        TenantExternalCallbackRequest callback,
        CancellationToken ct = default);
}

/// <summary>Redirect metadata for starting an external tenant sign-in flow.</summary>
public sealed record TenantExternalChallengeResult(string RedirectUrl, string CallbackUrl, string State);

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
