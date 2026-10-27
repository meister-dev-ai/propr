// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     Validates OIDC <c>id_token</c>s against per-authority discovery documents. The discovery document
///     and its JWKS are resolved and cached per metadata address; each token is checked for a valid
///     signature, audience, lifetime, and issuer before any claim is trusted.
/// </summary>
/// <remarks>
///     The issuer is pinned to the concrete issuer published by the authority's discovery document, so a
///     token is accepted only when its <c>iss</c> exactly matches that authority. Multi-tenant Microsoft
///     Entra authorities (<c>common</c>/<c>organizations</c>/<c>consumers</c>) are not supported: they are
///     rejected before reaching this validator, because each tenant is configured against its own specific
///     authority.
/// </remarks>
public sealed class OidcTokenValidator : IOidcTokenValidator
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(60);

    private readonly Func<string, BaseConfigurationManager> _configurationManagerFactory;

    private readonly ConcurrentDictionary<string, BaseConfigurationManager> _configurationManagers =
        new(StringComparer.Ordinal);

    private readonly ILogger<OidcTokenValidator> _logger;

    private readonly bool _allowPlainHttpEndpoints;

    /// <summary>Initializes a new instance of the <see cref="OidcTokenValidator" /> class.</summary>
    /// <param name="configurationManagerFactory">
    ///     Builds a cached <see cref="BaseConfigurationManager" /> for a given metadata address. Production wires a
    ///     network-backed manager; tests inject a static configuration so no network call is made.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="allowPlainHttpEndpoints">
    ///     Whether a discovery document may name plain http endpoints, which the host allows in Development only.
    /// </param>
    public OidcTokenValidator(
        Func<string, BaseConfigurationManager> configurationManagerFactory,
        ILogger<OidcTokenValidator> logger,
        bool allowPlainHttpEndpoints = false)
    {
        this._configurationManagerFactory = configurationManagerFactory;
        this._logger = logger;
        this._allowPlainHttpEndpoints = allowPlainHttpEndpoints;
    }

    /// <inheritdoc />
    public async Task<OidcTokenValidationResult> ValidateAsync(OidcTokenValidationRequest request, CancellationToken ct = default)
    {
        var discovery = await this.TryGetConfigurationAsync(request.MetadataAddress, ct).ConfigureAwait(false);
        if (discovery is null)
        {
            return OidcTokenValidationResult.Failure("oidc_discovery_unavailable");
        }

        var (configurationManager, configuration) = discovery.Value;
        var discoveryIssuer = configuration.Issuer;
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,

            // Also hand the handler the manager so a signing-key miss (IdP key rotation) triggers a
            // JWKS refresh-and-retry instead of failing every login until the periodic refresh fires.
            ConfigurationManager = configurationManager,
            ValidateIssuer = true,
            ValidIssuer = discoveryIssuer,
            ValidateAudience = true,
            ValidAudience = request.ExpectedAudience,
            ValidateLifetime = true,
            ClockSkew = AllowedClockSkew,
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(request.IdToken, validationParameters, out var validatedToken);

            // The nonce ties the token to the authorization request this sign-in sent. A token issued for another
            // request, and replayed here, carries another nonce or none.
            if (request.ExpectedNonce is not null
                && !string.Equals(principal.FindFirst("nonce")?.Value, request.ExpectedNonce, StringComparison.Ordinal))
            {
                return OidcTokenValidationResult.Failure("invalid_nonce");
            }

            return OidcTokenValidationResult.Success(principal, validatedToken.Issuer);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            return OidcTokenValidationResult.Failure("invalid_id_token_signature");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return OidcTokenValidationResult.Failure("invalid_id_token_signature");
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return OidcTokenValidationResult.Failure("unexpected_token_issuer");
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return OidcTokenValidationResult.Failure("unexpected_token_audience");
        }
        catch (SecurityTokenException)
        {
            return OidcTokenValidationResult.Failure("invalid_id_token");
        }
        catch (ArgumentException)
        {
            return OidcTokenValidationResult.Failure("invalid_id_token");
        }
    }

    /// <inheritdoc />
    public async Task<OidcProviderMetadata?> GetProviderMetadataAsync(string metadataAddress, CancellationToken ct = default)
    {
        var discovery = await this.TryGetConfigurationAsync(metadataAddress, ct).ConfigureAwait(false);
        if (discovery?.Configuration is not OpenIdConnectConfiguration configuration)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuration.Issuer)
            || !Uri.TryCreate(configuration.AuthorizationEndpoint, UriKind.Absolute, out var authorizationEndpoint)
            || !Uri.TryCreate(configuration.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint))
        {
            this._logger.LogWarning(
                "OidcDiscoveryIncomplete MetadataAddress={MetadataAddress}",
                metadataAddress);
            return null;
        }

        // The browser enters its credentials at the authorization endpoint, and the token request carries the client
        // secret, the code and the code verifier. A document fetched over https can still name plain http addresses
        // for them, so their scheme is checked here.
        if (!this.IsSecure(authorizationEndpoint) || !this.IsSecure(tokenEndpoint))
        {
            this._logger.LogWarning(
                "OidcDiscoveryInsecureEndpoint MetadataAddress={MetadataAddress}",
                metadataAddress);
            return null;
        }

        return new OidcProviderMetadata(configuration.Issuer, authorizationEndpoint, tokenEndpoint);
    }

    private bool IsSecure(Uri endpoint)
    {
        return endpoint.Scheme == Uri.UriSchemeHttps
               || (this._allowPlainHttpEndpoints && endpoint.Scheme == Uri.UriSchemeHttp);
    }

    private async Task<(BaseConfigurationManager Manager, BaseConfiguration Configuration)?> TryGetConfigurationAsync(
        string metadataAddress,
        CancellationToken ct)
    {
        try
        {
            var configurationManager = this._configurationManagers.GetOrAdd(metadataAddress, this._configurationManagerFactory);
            var configuration = await configurationManager.GetBaseConfigurationAsync(ct).ConfigureAwait(false);
            return (configurationManager, configuration);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation — propagate; do not mask it as a discovery failure.
            throw;
        }
        catch (Exception exception)
        {
            // Any other failure (including a discovery-fetch timeout, which surfaces as a
            // TaskCanceledException while the caller's token is NOT cancelled) fails closed.
            this._logger.LogWarning(
                exception,
                "OidcDiscoveryUnavailable MetadataAddress={MetadataAddress}",
                metadataAddress);
            return null;
        }
    }
}
