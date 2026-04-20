// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>HS256-signed JWT service using MEISTER_JWT_SECRET.</summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(IConfiguration configuration)
    {
        this._handler.MapInboundClaims = false;

        var secret = configuration["MEISTER_JWT_SECRET"]
                     ?? throw new InvalidOperationException(
                         "MEISTER_JWT_SECRET is required for JWT token issuance. " +
                         "Set it to a cryptographically-random string of at least 32 characters.");

        if (secret.Length < 32)
        {
            throw new InvalidOperationException("MEISTER_JWT_SECRET must be at least 32 characters long (256 bits).");
        }

        this._signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    /// <inheritdoc />
    public string GenerateAccessToken(AppUser user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("global_role", user.GlobalRole.ToString()),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(this._signingKey, SecurityAlgorithms.HmacSha256),
            Issuer = "meisterpropr",
            Audience = "meisterpropr",
        };

        var token = this._handler.CreateToken(descriptor);
        return this._handler.WriteToken(token);
    }

    /// <inheritdoc />
    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "meisterpropr",
                ValidateAudience = true,
                ValidAudience = "meisterpropr",
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = this._signingKey,
                ClockSkew = TimeSpan.FromSeconds(30),
            };

            return this._handler.ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }
}
