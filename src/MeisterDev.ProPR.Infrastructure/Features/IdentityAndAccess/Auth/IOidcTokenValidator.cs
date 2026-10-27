// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Claims;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     Reads the discovery document of an OpenID Connect provider and cryptographically validates the <c>id_token</c>
///     that a sign-in through that provider returns: signature (JWKS from discovery), issuer, audience, lifetime, and
///     the nonce when the sign-in sent one.
/// </summary>
public interface IOidcTokenValidator
{
    /// <summary>
    ///     Validates <paramref name="request" />'s <c>id_token</c> against the provider's discovery document.
    /// </summary>
    /// <param name="request">The token, the provider's OIDC metadata address, the expected audience and nonce.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A successful result carrying the validated principal and issuer, or a failure with a stable code.</returns>
    Task<OidcTokenValidationResult> ValidateAsync(OidcTokenValidationRequest request, CancellationToken ct = default);

    /// <summary>
    ///     Returns the issuer and the endpoints that the provider's discovery document publishes. The document is
    ///     cached together with the signing keys that <see cref="ValidateAsync" /> uses.
    /// </summary>
    /// <param name="metadataAddress">The provider's discovery document URL (<c>.../.well-known/openid-configuration</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The metadata, or <see langword="null" /> when the document cannot be read or names no authorization or token
    ///     endpoint.
    /// </returns>
    Task<OidcProviderMetadata?> GetProviderMetadataAsync(string metadataAddress, CancellationToken ct = default);
}

/// <summary>Inputs required to validate an OIDC <c>id_token</c>.</summary>
/// <param name="IdToken">The raw JWT <c>id_token</c>.</param>
/// <param name="MetadataAddress">The provider's OIDC discovery document URL (<c>.../.well-known/openid-configuration</c>).</param>
/// <param name="ExpectedAudience">The audience the token must carry — the provider's configured client id.</param>
/// <param name="ExpectedNonce">
///     The nonce the authorization request sent, which the token must carry in its <c>nonce</c> claim, or
///     <see langword="null" /> when the request sent none.
/// </param>
public sealed record OidcTokenValidationRequest(
    string IdToken,
    string MetadataAddress,
    string ExpectedAudience,
    string? ExpectedNonce = null);

/// <summary>Outcome of validating an OIDC <c>id_token</c>.</summary>
/// <param name="IsValid">Whether the token passed signature, issuer, audience, lifetime and nonce validation.</param>
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

/// <summary>The issuer and the endpoints an OpenID Connect provider publishes in its discovery document.</summary>
/// <param name="Issuer">The issuer that every token of the provider carries.</param>
/// <param name="AuthorizationEndpoint">The endpoint the browser is sent to for the sign-in.</param>
/// <param name="TokenEndpoint">The endpoint that redeems an authorization code.</param>
public sealed record OidcProviderMetadata(string Issuer, Uri AuthorizationEndpoint, Uri TokenEndpoint);
