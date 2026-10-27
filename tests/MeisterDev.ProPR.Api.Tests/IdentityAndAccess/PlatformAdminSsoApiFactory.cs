// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The host of <see cref="TenantAdministrationApiFactory" /> with an identity provider for platform administrators
///     configured. The provider's discovery document is the static one of the base factory, and its token endpoint
///     answers from the queue of external sign-in responses.
/// </summary>
public sealed class PlatformAdminSsoApiFactory : TenantAdministrationApiFactory
{
    public const string Authority = "https://id.example.test/realms/staff";
    public const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";
    public const string TokenEndpoint = Authority + "/protocol/openid-connect/token";
    public const string ClientId = "propr-platform";
    public const string ClientSecret = "platform-client-secret";
    public const string RequiredRole = "operator";
    public const string DisplayName = "Meister DEV staff";

    /// <summary>An origin of the user interface that the installation lists in <c>CORS_ORIGINS</c>.</summary>
    public const string ConsoleOrigin = "https://console.example.test";

    private int _refreshTokenRevocationFailures;

    /// <summary>The audit log of the host, which the tests read the recorded events from.</summary>
    public IUserAccountAuditLog AuditLog { get; } = Substitute.For<IUserAccountAuditLog>();

    /// <summary>Makes the next revocation of refresh tokens fail, as a database failure in the middle of a sign-in.</summary>
    public void FailNextRefreshTokenRevocation()
    {
        Interlocked.Exchange(ref this._refreshTokenRevocationFailures, 1);
    }

    private bool TakeRefreshTokenRevocationFailure()
    {
        return Interlocked.Exchange(ref this._refreshTokenRevocationFailures, 0) == 1;
    }

    /// <summary>A client that keeps cookies and does not follow redirects, as a browser on the sign-in pages.</summary>
    public HttpClient CreateBrowser()
    {
        return this.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>Returns an ID token of the provider, signed with the key the host trusts.</summary>
    public static string CreateIdToken(
        string subject,
        string? email,
        string? nonce,
        IReadOnlyCollection<string> roles,
        string? preferredUsername = null)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("email_verified", "true", ClaimValueTypes.Boolean));
        }

        if (nonce is not null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        if (preferredUsername is not null)
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }

        claims.AddRange(roles.Select(role => new Claim("roles", role)));

        var token = new JwtSecurityToken(
            Authority,
            ClientId,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: OidcSigningCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting(PlatformAdminSsoOptions.AuthorityKey, Authority);
        builder.UseSetting(PlatformAdminSsoOptions.ClientIdKey, ClientId);
        builder.UseSetting(PlatformAdminSsoOptions.ClientSecretKey, ClientSecret);
        builder.UseSetting(PlatformAdminSsoOptions.RequiredRoleKey, RequiredRole);
        builder.UseSetting(PlatformAdminSsoOptions.DisplayNameKey, DisplayName);
        builder.UseSetting("CORS_ORIGINS", ConsoleOrigin);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserAccountAuditLog>();
            services.AddSingleton(this.AuditLog);
            services.AddScoped<IPlatformAdminSignInService, PlatformAdminSignInService>();
            RowByRowRevocations.Register(services, this.TakeRefreshTokenRevocationFailure);
        });
    }
}
