// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.ExtensionAuth;

public sealed class ExtensionJwtTests
{
    private const string Issuer = "https://propr.test/api";
    private const string Key = "test-only-signing-key-with-at-least-32-characters";

    private readonly JwtTokenService jwt = new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MEISTER_JWT_SECRET"] = Key,
                ["MEISTER_EXTENSION_AUTH_ISSUER"] = Issuer,
                ["MEISTER_EXTENSION_AUTH_REDIRECT_URIS"] = "[\"https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr\"]",
            }).Build());

    [Fact]
    public void ExtensionAccessUsesCanonicalIssuerAudienceAndSessionBindings()
    {
        var grant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var user = new AppUser { Id = Guid.NewGuid(), Username = "alice" };
        var token = this.jwt.GenerateExtensionAccessToken(user, grant, session);
        var principal = this.jwt.ValidateAccessToken(token);
        Assert.NotNull(principal);
        Assert.True(principal.HasClaim("iss", Issuer));
        Assert.True(principal.HasClaim("aud", "propr-lens"));
        Assert.True(principal.HasClaim("extension_grant_id", grant.ToString()));
        Assert.True(principal.HasClaim("extension_session_id", session.ToString()));
        Assert.NotNull(this.jwt.ValidateAccessToken(this.jwt.GenerateAccessToken(user)));
    }

    [Theory]
    [InlineData("meisterpropr", "propr-lens")]
    [InlineData(Issuer, "meisterpropr")]
    [InlineData("https://other.test/api", "propr-lens")]
    public void IncorrectIssuerAudiencePairsAreRejectedEvenWithTheSameSigningKey(string issuer, string audience)
    {
        var token = new JwtSecurityToken(
            issuer, audience, [new Claim("sub", Guid.NewGuid().ToString())], expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        Assert.Null(this.jwt.ValidateAccessToken(new JwtSecurityTokenHandler().WriteToken(token)));
    }
}
