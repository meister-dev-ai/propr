// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>HS256-signed JWT service using MEISTER_JWT_SECRET.</summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _signingKey;
    private readonly string? _extensionIssuer;

    /// <summary>Creates the service and reads the extension sign-in settings from <paramref name="configuration" />.</summary>
    public JwtTokenService(IConfiguration configuration)
        : this(configuration, ExtensionAuthorizationOptions.FromConfiguration(configuration))
    {
    }

    /// <summary>
    ///     Creates the service with the extension sign-in settings that the host registered. Dependency injection uses this
    ///     constructor, so the host parses the extension settings once.
    /// </summary>
    public JwtTokenService(IConfiguration configuration, ExtensionAuthorizationOptions extensionAuthorization)
    {
        ArgumentNullException.ThrowIfNull(extensionAuthorization);
        this._handler.MapInboundClaims = false;
        this._extensionIssuer = extensionAuthorization.Issuer;

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
        => this.Generate(user, "meisterpropr", "meisterpropr", []);

    /// <inheritdoc />
    public string GenerateExtensionAccessToken(AppUser user, Guid grantId, Guid sessionId)
        => this.Generate(
            user, "propr-lens", this._extensionIssuer ?? throw new InvalidOperationException("Extension issuer is not configured."),
            [new Claim("extension_grant_id", grantId.ToString()), new Claim("extension_session_id", sessionId.ToString())]);

    private string Generate(AppUser user, string audience, string issuer, Claim[] additional)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("global_role", user.GlobalRole.ToString()),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims.Concat(additional)),
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(this._signingKey, SecurityAlgorithms.HmacSha256),
            Issuer = issuer,
            Audience = audience,
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
                IssuerValidator = (issuer, token, _) =>
                {
                    if (token is JwtSecurityToken jwt && jwt.Audiences.ToArray() is [var audience]
                                                      && ((audience == "meisterpropr" && issuer == "meisterpropr")
                                                          || (audience == "propr-lens" && this._extensionIssuer is not null &&
                                                              issuer == this._extensionIssuer)))
                    {
                        return issuer;
                    }

                    throw new SecurityTokenInvalidIssuerException("Invalid issuer and audience pair.");
                },
                ValidateAudience = true,
                ValidAudiences = ["meisterpropr", "propr-lens"],
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
