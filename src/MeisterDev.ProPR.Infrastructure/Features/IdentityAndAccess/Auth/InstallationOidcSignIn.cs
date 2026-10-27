// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Claims;
using System.Text.Json;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     The sign-in through an OpenID Connect identity provider that the installation configures, from the redirect to the
///     provider to the validated identity. ProPR uses the authorization code flow with PKCE (S256) and a nonce, and keeps
///     the state of the sign-in in a protected cookie of the browser that started it. ProPR accepts each state once and
///     validates the ID token against the discovery document of the provider, whose issuer must be the configured
///     authority.
/// </summary>
/// <remarks>
///     The sign-in of platform administrators and the sign-in of provisioned users use this flow. Each of them decides
///     which account the identity at the provider reaches.
/// </remarks>
public sealed class InstallationOidcSignIn(
    ISecretProtectionCodec secretProtectionCodec,
    IHttpClientFactory httpClientFactory,
    IOidcTokenValidator oidcTokenValidator,
    ExternalSignInRedemptions redemptions,
    ILogger<InstallationOidcSignIn> logger)
{
    private const string NotConfirmedMessage = "The identity provider did not confirm the sign-in. Start the sign-in again.";

    private const string InvalidStateMessage = "The sign-in request is invalid or has expired. Start the sign-in again.";

    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Builds the redirect to the provider and the protected state of the sign-in.</summary>
    /// <param name="kind">The kind of sign-in.</param>
    /// <param name="provider">The settings of the provider.</param>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="frontendReturnUrl">The frontend page the sign-in returns to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The challenge, or <see langword="null" /> when the provider's discovery document cannot be used.</returns>
    public async Task<InstallationSignInChallenge?> BuildChallengeAsync(
        InstallationSignInKind kind,
        InstallationSsoProviderOptions provider,
        Uri applicationBaseUri,
        Uri frontendReturnUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(frontendReturnUrl);

        var metadata = await this.GetProviderMetadataAsync(kind, provider, ct);
        if (metadata is null)
        {
            return null;
        }

        var flow = new InstallationSignInFlow(
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            BuildCallbackUrl(kind, applicationBaseUri),
            frontendReturnUrl.ToString(),
            DateTimeOffset.UtcNow);

        var redirectUri = AuthorizationCodeFlow.BuildAuthorizationUri(
            metadata.AuthorizationEndpoint,
            [
                new KeyValuePair<string, string>("client_id", provider.ClientId!),
                new KeyValuePair<string, string>("response_type", "code"),
                new KeyValuePair<string, string>("redirect_uri", flow.CallbackUrl),
                new KeyValuePair<string, string>("scope", "openid profile email"),
                new KeyValuePair<string, string>("state", flow.State),
                new KeyValuePair<string, string>("nonce", flow.Nonce),
                new KeyValuePair<string, string>("code_challenge", AuthorizationCodeFlow.ComputeCodeChallenge(flow.CodeVerifier)),
                new KeyValuePair<string, string>("code_challenge_method", "S256"),
            ]);

        return new InstallationSignInChallenge(
            redirectUri.ToString(),
            metadata.AuthorizationEndpoint.GetLeftPart(UriPartial.Authority),
            ProtectedSignInState.Protect(secretProtectionCodec, flow, kind.FlowPurpose));
    }

    /// <summary>
    ///     Completes the flow from the provider's callback: checks the state against the cookie of the browser, records
    ///     the state as used, redeems the code with the code verifier, and validates the ID token with the nonce.
    /// </summary>
    /// <param name="kind">The kind of sign-in.</param>
    /// <param name="provider">The settings of the provider.</param>
    /// <param name="applicationBaseUri">Public base URI of the API, used for the callback address.</param>
    /// <param name="callback">The query parameters of the callback.</param>
    /// <param name="protectedFlow">The protected state that the challenge stored in the browser.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The identity at the provider, or the reason the flow refused the sign-in.</returns>
    public async Task<InstallationOidcSignInResult> CompleteAsync(
        InstallationSignInKind kind,
        InstallationSsoProviderOptions provider,
        Uri applicationBaseUri,
        InstallationSignInCallback callback,
        string? protectedFlow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(callback);

        var flow = ProtectedSignInState.TryRead<InstallationSignInFlow>(secretProtectionCodec, protectedFlow, kind.FlowPurpose);
        if (flow is null
            || !string.Equals(flow.State, callback.State, StringComparison.Ordinal)
            || !string.Equals(flow.CallbackUrl, BuildCallbackUrl(kind, applicationBaseUri), StringComparison.Ordinal)
            || flow.IssuedAtUtc + FlowLifetime < DateTimeOffset.UtcNow)
        {
            return this.Refuse(kind, "invalid_state", InvalidStateMessage, flow?.FrontendReturnUrl);
        }

        // The state is accepted once. A second callback with the same state and cookie, sent again by the browser or by
        // someone who copied both, is refused before ProPR asks the provider.
        if (!await redemptions.TryRedeemAsync(flow.State, flow.IssuedAtUtc + FlowLifetime, ct))
        {
            logger.LogWarning("InstallationSignInRejected SignIn={SignIn} Reason={Reason}", kind.Name, "state_already_used");
            return new InstallationOidcSignInResult(null, flow.FrontendReturnUrl, "invalid_state", InvalidStateMessage);
        }

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            return this.Refuse(
                kind,
                "provider_error",
                callback.ErrorDescription ?? "The identity provider refused the sign-in.",
                flow.FrontendReturnUrl,
                callback.Error);
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            return this.Refuse(
                kind,
                "missing_authorization_code",
                "The identity provider did not return an authorization code.",
                flow.FrontendReturnUrl);
        }

        var metadata = await this.GetProviderMetadataAsync(kind, provider, ct);
        if (metadata is null)
        {
            return this.Refuse(kind, "provider_unavailable", kind.UnavailableMessage, flow.FrontendReturnUrl);
        }

        var (identity, failureCode) = await this.RedeemAndValidateAsync(kind, provider, metadata, flow, callback.Code.Trim(), ct);
        return identity is null
            ? this.Refuse(kind, failureCode!, NotConfirmedMessage, flow.FrontendReturnUrl)
            : new InstallationOidcSignInResult(identity, flow.FrontendReturnUrl);
    }

    private static string BuildCallbackUrl(InstallationSignInKind kind, Uri applicationBaseUri)
    {
        return new Uri(applicationBaseUri, kind.CallbackPath).ToString();
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        // A token endpoint can answer with valid JSON that is not an object, such as [] or null. TryGetProperty throws
        // for such a value, so the answer is treated as one without the property.
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private async Task<OidcProviderMetadata?> GetProviderMetadataAsync(
        InstallationSignInKind kind,
        InstallationSsoProviderOptions provider,
        CancellationToken ct)
    {
        var metadata = await oidcTokenValidator.GetProviderMetadataAsync(provider.MetadataAddress, ct);
        if (metadata is null)
        {
            logger.LogWarning(
                "InstallationSignInUnavailable SignIn={SignIn} MetadataAddress={MetadataAddress} Reason={Reason}",
                kind.Name,
                SecretSafeRendering.Address(provider.MetadataAddress),
                "oidc_discovery_unavailable");
            return null;
        }

        // The discovery document names the issuer every token is validated against. It must be the configured
        // authority, so a metadata address that points at another realm or provider cannot widen whose tokens count.
        if (!provider.IsAuthority(metadata.Issuer))
        {
            logger.LogWarning(
                "InstallationSignInUnavailable SignIn={SignIn} MetadataAddress={MetadataAddress} Reason={Reason} Issuer={Issuer}",
                kind.Name,
                SecretSafeRendering.Address(provider.MetadataAddress),
                "issuer_mismatch",
                metadata.Issuer);
            return null;
        }

        return metadata;
    }

    private async Task<(InstallationOidcIdentity? Identity, string? FailureCode)> RedeemAndValidateAsync(
        InstallationSignInKind kind,
        InstallationSsoProviderOptions provider,
        OidcProviderMetadata metadata,
        InstallationSignInFlow flow,
        string authorizationCode,
        CancellationToken ct)
    {
        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName),
            metadata.TokenEndpoint,
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", provider.ClientId!),
                new KeyValuePair<string, string>("client_secret", provider.ClientSecret!),
                new KeyValuePair<string, string>("code", authorizationCode),
                new KeyValuePair<string, string>("redirect_uri", flow.CallbackUrl),
                new KeyValuePair<string, string>("code_verifier", flow.CodeVerifier),
            ],
            null,
            logger,
            ct);

        using (document)
        {
            if (document is null)
            {
                logger.LogWarning(
                    "InstallationSignInRejected SignIn={SignIn} Reason={Reason} StatusCode={StatusCode}",
                    kind.Name,
                    "token_exchange_failed",
                    (int?)statusCode);
                return (null, "token_exchange_failed");
            }

            if (!TryGetString(document.RootElement, "id_token", out var idToken))
            {
                return (null, "missing_id_token");
            }

            var validation = await oidcTokenValidator.ValidateAsync(
                new OidcTokenValidationRequest(idToken, provider.MetadataAddress, provider.ClientId!, flow.Nonce),
                ct);
            if (!validation.IsValid || validation.Principal is null || !provider.IsAuthority(validation.Issuer))
            {
                return (null, validation.FailureCode ?? "unexpected_token_issuer");
            }

            // The subject is an opaque identifier and is compared as the token carries it; " alice" is not "alice".
            var subject = validation.Principal.FindFirst("sub")?.Value;
            return string.IsNullOrWhiteSpace(subject)
                ? (null, "missing_identity_claims")
                : (new InstallationOidcIdentity(validation.Issuer!, subject, validation.Principal), null);
        }
    }

    private InstallationOidcSignInResult Refuse(
        InstallationSignInKind kind,
        string failureCode,
        string failureMessage,
        string? frontendReturnUrl,
        string? providerError = null)
    {
        logger.LogWarning(
            "InstallationSignInRejected SignIn={SignIn} Reason={Reason} ProviderError={ProviderError}",
            kind.Name,
            failureCode,
            providerError?.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
        return new InstallationOidcSignInResult(null, frontendReturnUrl, failureCode, failureMessage);
    }

    /// <summary>The state of one sign-in, which the browser keeps in a protected cookie until the callback.</summary>
    private sealed record InstallationSignInFlow(
        string State,
        string Nonce,
        string CodeVerifier,
        string CallbackUrl,
        string FrontendReturnUrl,
        DateTimeOffset IssuedAtUtc)
    {
        /// <summary>Renders the flow without its nonce and code verifier; see <see cref="SecretSafeRendering" />.</summary>
        public override string ToString()
        {
            return $"{nameof(InstallationSignInFlow)} {{ CallbackUrl = {this.CallbackUrl}, "
                   + $"CodeVerifier = {SecretSafeRendering.Elide(this.CodeVerifier)}, IssuedAtUtc = {this.IssuedAtUtc:O} }}";
        }
    }
}

/// <summary>A kind of sign-in through an identity provider of the installation.</summary>
/// <param name="Name">The name of the kind in the log, for example <c>platform_admin</c>.</param>
/// <param name="CallbackPath">The path of the callback below the public base URL of the API, which the provider redirects to.</param>
/// <param name="FlowPurpose">
///     The purpose that the protection of the state is bound to, so that one kind of sign-in cannot read the state of
///     another.
/// </param>
/// <param name="UnavailableMessage">What the person signing in reads when the provider cannot be reached.</param>
public sealed record InstallationSignInKind(string Name, string CallbackPath, string FlowPurpose, string UnavailableMessage);

/// <summary>The outcome of the flow: the identity at the provider, or the reason the flow refused the sign-in.</summary>
/// <param name="Identity">The identity, or <see langword="null" /> when the flow refused the sign-in.</param>
/// <param name="FrontendReturnUrl">The frontend page the sign-in returns to, when it is known.</param>
/// <param name="FailureCode">A stable code for the refusal.</param>
/// <param name="FailureMessage">What the person signing in reads about the refusal.</param>
public sealed record InstallationOidcSignInResult(
    InstallationOidcIdentity? Identity,
    string? FrontendReturnUrl,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>The identity at the provider, as the validated ID token states it.</summary>
/// <param name="Issuer">The issuer of the token, which names the configured authority.</param>
/// <param name="Subject">The subject, as the token carries it.</param>
/// <param name="Principal">The claims of the validated token.</param>
public sealed record InstallationOidcIdentity(string Issuer, string Subject, ClaimsPrincipal Principal);
