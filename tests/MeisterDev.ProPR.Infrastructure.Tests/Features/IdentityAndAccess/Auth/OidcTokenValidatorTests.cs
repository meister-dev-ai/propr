// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using MeisterDev.ProPR.Infrastructure.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

public sealed class OidcTokenValidatorTests
{
    private const string TestAudience = "test-client-id";
    private const string SingleTenantIssuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";
    private const string MetadataAddress = SingleTenantIssuer + "/.well-known/openid-configuration";

    [Fact]
    public async Task ValidateAsync_WithCorrectlySignedSingleTenantToken_Succeeds()
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(SigningCredentialsFor(key), SingleTenantIssuer, TestAudience, "user-1");

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience));

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
        Assert.Equal(SingleTenantIssuer, result.Issuer);
        Assert.Equal("user-1", result.Principal?.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task ValidateAsync_WithTokenSignedByUnknownKey_RejectsSignature()
    {
        var configuredKey = CreateRsaKey("k1");
        var attackerKey = CreateRsaKey("k2");
        var validator = CreateValidator(SingleTenantIssuer, configuredKey);
        var token = CreateSignedToken(SigningCredentialsFor(attackerKey), SingleTenantIssuer, TestAudience, "user-1");

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_id_token_signature", result.FailureCode);
    }

    [Fact]
    public async Task ValidateAsync_WithSingleTenantIssuerMismatch_RejectsIssuer()
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(SigningCredentialsFor(key), "https://login.microsoftonline.com/other-tenant/v2.0", TestAudience, "user-1");

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience));

        Assert.False(result.IsValid);
        Assert.Equal("unexpected_token_issuer", result.FailureCode);
    }

    [Fact]
    public async Task ValidateAsync_WithExpiredToken_RejectsToken()
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(
            SigningCredentialsFor(key),
            SingleTenantIssuer,
            TestAudience,
            "user-1",
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            expires: DateTime.UtcNow.AddMinutes(-10));

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_id_token", result.FailureCode);
    }

    [Fact]
    public async Task ValidateAsync_WithWrongAudience_RejectsAudience()
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(SigningCredentialsFor(key), SingleTenantIssuer, "some-other-audience", "user-1");

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience));

        Assert.False(result.IsValid);
        Assert.Equal("unexpected_token_audience", result.FailureCode);
    }

    [Fact]
    public async Task ValidateAsync_WithTheNonceOfTheRequest_Succeeds()
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(SigningCredentialsFor(key), SingleTenantIssuer, TestAudience, "user-1", nonce: "nonce-1");

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience, "nonce-1"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("nonce-of-another-request")]
    [InlineData(null)]
    public async Task ValidateAsync_WithoutTheNonceOfTheRequest_RejectsToken(string? tokenNonce)
    {
        var key = CreateRsaKey("k1");
        var validator = CreateValidator(SingleTenantIssuer, key);
        var token = CreateSignedToken(SigningCredentialsFor(key), SingleTenantIssuer, TestAudience, "user-1", nonce: tokenNonce);

        var result = await validator.ValidateAsync(new OidcTokenValidationRequest(token, MetadataAddress, TestAudience, "nonce-1"));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_nonce", result.FailureCode);
    }

    [Fact]
    public async Task GetProviderMetadataAsync_ReturnsTheIssuerAndTheEndpointsOfTheDiscoveryDocument()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = SingleTenantIssuer,
            AuthorizationEndpoint = "https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize",
            TokenEndpoint = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token",
        };
        var validator = CreateValidator(configuration);

        var metadata = await validator.GetProviderMetadataAsync(MetadataAddress);

        Assert.NotNull(metadata);
        Assert.Equal(SingleTenantIssuer, metadata.Issuer);
        Assert.Equal(new Uri(configuration.AuthorizationEndpoint), metadata.AuthorizationEndpoint);
        Assert.Equal(new Uri(configuration.TokenEndpoint), metadata.TokenEndpoint);
    }

    [Fact]
    public async Task GetProviderMetadataAsync_WithoutATokenEndpoint_ReturnsNull()
    {
        var validator = CreateValidator(
            new OpenIdConnectConfiguration
            {
                Issuer = SingleTenantIssuer,
                AuthorizationEndpoint = "https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize",
            });

        Assert.Null(await validator.GetProviderMetadataAsync(MetadataAddress));
    }

    [Theory]
    [InlineData("http://login.example.test/authorize", "https://login.example.test/token")]
    [InlineData("https://login.example.test/authorize", "http://login.example.test/token")]
    [InlineData("https://login.example.test/authorize", "ftp://login.example.test/token")]
    public async Task GetProviderMetadataAsync_OutsideDevelopment_RefusesEndpointsWithoutHttps(
        string authorizationEndpoint,
        string tokenEndpoint)
    {
        var validator = CreateValidator(
            new OpenIdConnectConfiguration
            {
                Issuer = SingleTenantIssuer,
                AuthorizationEndpoint = authorizationEndpoint,
                TokenEndpoint = tokenEndpoint,
            });

        Assert.Null(await validator.GetProviderMetadataAsync(MetadataAddress));
    }

    [Fact]
    public async Task GetProviderMetadataAsync_InDevelopment_AcceptsPlainHttpEndpoints()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = SingleTenantIssuer,
            AuthorizationEndpoint = "http://localhost:8080/realms/staff/protocol/openid-connect/auth",
            TokenEndpoint = "http://localhost:8080/realms/staff/protocol/openid-connect/token",
        };
        var validator = new OidcTokenValidator(
            _ => new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            Substitute.For<ILogger<OidcTokenValidator>>(),
            allowPlainHttpEndpoints: true);

        var metadata = await validator.GetProviderMetadataAsync(MetadataAddress);

        Assert.NotNull(metadata);
        Assert.Equal(new Uri(configuration.TokenEndpoint), metadata.TokenEndpoint);
    }

    [Fact]
    public async Task GetProviderMetadataAsync_WhenTheDiscoveryDocumentCannotBeRead_ReturnsNull()
    {
        var validator = new OidcTokenValidator(
            _ => throw new InvalidOperationException("The discovery document is unreachable."),
            Substitute.For<ILogger<OidcTokenValidator>>());

        Assert.Null(await validator.GetProviderMetadataAsync(MetadataAddress));
    }

    private static OidcTokenValidator CreateValidator(string issuer, SecurityKey signingKey)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(signingKey);
        return CreateValidator(configuration);
    }

    private static OidcTokenValidator CreateValidator(OpenIdConnectConfiguration configuration)
    {
        return new OidcTokenValidator(
            _ => new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            Substitute.For<ILogger<OidcTokenValidator>>());
    }

    private static RsaSecurityKey CreateRsaKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };

    private static SigningCredentials SigningCredentialsFor(SecurityKey key) => new(key, SecurityAlgorithms.RsaSha256);

    private static string CreateSignedToken(
        SigningCredentials credentials,
        string issuer,
        string audience,
        string subject,
        DateTime? notBefore = null,
        DateTime? expires = null,
        string? nonce = null)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (nonce is not null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore ?? DateTime.UtcNow.AddMinutes(-5),
            expires ?? DateTime.UtcNow.AddMinutes(5),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
