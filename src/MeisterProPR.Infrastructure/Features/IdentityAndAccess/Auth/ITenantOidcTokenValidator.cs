// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>
///     Cryptographically validates an OIDC <c>id_token</c> returned from a tenant SSO code exchange
///     against the provider's published signing keys (JWKS from OIDC discovery), issuer, audience, and lifetime.
/// </summary>
public interface ITenantOidcTokenValidator
{
    /// <summary>
    ///     Validates <paramref name="request" />'s <c>id_token</c> against the provider's discovery document.
    /// </summary>
    /// <param name="request">The token, the provider's OIDC metadata address, and the expected audience.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A successful result carrying the validated principal and issuer, or a failure with a stable code.</returns>
    Task<OidcTokenValidationResult> ValidateAsync(OidcTokenValidationRequest request, CancellationToken ct = default);
}

/// <summary>Inputs required to validate an OIDC <c>id_token</c>.</summary>
/// <param name="IdToken">The raw JWT <c>id_token</c>.</param>
/// <param name="MetadataAddress">The provider's OIDC discovery document URL (<c>.../.well-known/openid-configuration</c>).</param>
/// <param name="ExpectedAudience">The audience the token must carry — the provider's configured client id.</param>
public sealed record OidcTokenValidationRequest(string IdToken, string MetadataAddress, string ExpectedAudience);

/// <summary>Outcome of validating an OIDC <c>id_token</c>.</summary>
/// <param name="IsValid">Whether the token passed signature, issuer, audience, and lifetime validation.</param>
/// <param name="FailureCode">A stable rejection code when <paramref name="IsValid" /> is <c>false</c>; otherwise <c>null</c>.</param>
/// <param name="Principal">The validated claims principal when successful; otherwise <c>null</c>.</param>
/// <param name="Issuer">The validated token issuer when successful; otherwise <c>null</c>.</param>
public sealed record OidcTokenValidationResult(bool IsValid, string? FailureCode, ClaimsPrincipal? Principal, string? Issuer)
{
    /// <summary>Creates a failed validation result with the given stable rejection code.</summary>
    public static OidcTokenValidationResult Failure(string failureCode) => new(false, failureCode, null, null);

    /// <summary>Creates a successful validation result carrying the validated principal and issuer.</summary>
    public static OidcTokenValidationResult Success(ClaimsPrincipal principal, string issuer) => new(true, null, principal, issuer);
}
