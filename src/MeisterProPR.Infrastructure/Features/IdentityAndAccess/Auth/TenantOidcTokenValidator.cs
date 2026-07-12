// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>
///     Validates OIDC <c>id_token</c>s against per-authority discovery documents. The discovery document
///     and its JWKS are resolved and cached per metadata address; each token is checked for a valid
///     signature, audience, lifetime, and issuer before any claim is trusted.
/// </summary>
/// <remarks>
///     Issuer handling supports both single- and multi-tenant authorities. When the discovery issuer is a
///     concrete value (single-tenant), it is pinned exactly. When the discovery issuer is templated with
///     <c>{tenantid}</c> (Microsoft Entra <c>common</c>/<c>organizations</c>/<c>consumers</c> authorities),
///     the token's own <c>tid</c> claim is substituted and the resulting issuer is required to match — so a
///     multi-tenant authority still only accepts Microsoft-signed tokens with a coherent tenant/issuer pair.
/// </remarks>
public sealed class TenantOidcTokenValidator : ITenantOidcTokenValidator
{
    private const string EntraTenantIdTemplate = "{tenantid}";
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(60);

    private readonly Func<string, BaseConfigurationManager> _configurationManagerFactory;

    private readonly ConcurrentDictionary<string, BaseConfigurationManager> _configurationManagers =
        new(StringComparer.Ordinal);

    private readonly ILogger<TenantOidcTokenValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="TenantOidcTokenValidator" /> class.</summary>
    /// <param name="configurationManagerFactory">
    ///     Builds a cached <see cref="BaseConfigurationManager" /> for a given metadata address. Production wires a
    ///     network-backed manager; tests inject a static configuration so no network call is made.
    /// </param>
    /// <param name="logger">Logger.</param>
    public TenantOidcTokenValidator(
        Func<string, BaseConfigurationManager> configurationManagerFactory,
        ILogger<TenantOidcTokenValidator> logger)
    {
        this._configurationManagerFactory = configurationManagerFactory;
        this._logger = logger;
    }

    /// <inheritdoc />
    public async Task<OidcTokenValidationResult> ValidateAsync(OidcTokenValidationRequest request, CancellationToken ct = default)
    {
        BaseConfigurationManager configurationManager;
        BaseConfiguration configuration;
        try
        {
            configurationManager = this._configurationManagers.GetOrAdd(request.MetadataAddress, this._configurationManagerFactory);
            configuration = await configurationManager.GetBaseConfigurationAsync(ct).ConfigureAwait(false);
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
                "TenantOidcDiscoveryUnavailable MetadataAddress={MetadataAddress}",
                request.MetadataAddress);
            return OidcTokenValidationResult.Failure("oidc_discovery_unavailable");
        }

        var discoveryIssuer = configuration.Issuer;
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,

            // Also hand the handler the manager so a signing-key miss (IdP key rotation) triggers a
            // JWKS refresh-and-retry instead of failing every login until the periodic refresh fires.
            ConfigurationManager = configurationManager,
            ValidateIssuer = true,
            IssuerValidator = (issuer, securityToken, _) => ValidateIssuer(issuer, securityToken, discoveryIssuer),
            ValidateAudience = true,
            ValidAudience = request.ExpectedAudience,
            ValidateLifetime = true,
            ClockSkew = AllowedClockSkew,
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(request.IdToken, validationParameters, out var validatedToken);
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

    private static string ValidateIssuer(string issuer, SecurityToken securityToken, string? discoveryIssuer)
    {
        if (string.IsNullOrWhiteSpace(discoveryIssuer) || string.IsNullOrWhiteSpace(issuer))
        {
            throw new SecurityTokenInvalidIssuerException("The token issuer or discovery issuer is missing.");
        }

        string expectedIssuer;
        if (discoveryIssuer.Contains(EntraTenantIdTemplate, StringComparison.OrdinalIgnoreCase))
        {
            var tenantId = (securityToken as JwtSecurityToken)?.Claims
                .FirstOrDefault(claim => claim.Type == "tid")?.Value;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new SecurityTokenInvalidIssuerException("A multi-tenant authority token is missing its 'tid' claim.");
            }

            expectedIssuer = discoveryIssuer.Replace(EntraTenantIdTemplate, tenantId, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            expectedIssuer = discoveryIssuer;
        }

        if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidIssuerException($"The token issuer '{issuer}' does not match the expected issuer.");
        }

        return issuer;
    }
}
