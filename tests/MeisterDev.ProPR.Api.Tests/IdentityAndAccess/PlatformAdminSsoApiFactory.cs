// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
            services.RemoveAll<IRefreshTokenRepository>();
            services.AddScoped<RefreshTokenRepository>();
            services.AddScoped<IRefreshTokenRepository>(serviceProvider => new RowByRowRefreshTokenRepository(
                serviceProvider.GetRequiredService<MeisterProPRDbContext>(),
                serviceProvider.GetRequiredService<RefreshTokenRepository>(),
                this.TakeRefreshTokenRevocationFailure));
            services.RemoveAll<IUserPatRepository>();
            services.AddScoped<UserPatRepository>();
            services.AddScoped<IUserPatRepository, RowByRowUserPatRepository>();
        });
    }

    /// <summary>
    ///     The refresh token repository of the host, with a revocation that the in-memory database runs. The
    ///     repository revokes with a bulk update, which the in-memory provider does not translate, so this one sets
    ///     the same column row by row.
    /// </summary>
    private sealed class RowByRowRefreshTokenRepository(
        MeisterProPRDbContext database,
        RefreshTokenRepository repository,
        Func<bool> takeRevocationFailure)
        : IRefreshTokenRepository
    {
        public Task AddAsync(RefreshToken token, CancellationToken ct = default)
        {
            return repository.AddAsync(token, ct);
        }

        public Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default)
        {
            return repository.GetActiveByHashAsync(tokenHash, ct);
        }

        public Task TouchLastUsedAsync(Guid id, DateTimeOffset lastUsedAt, CancellationToken ct = default)
        {
            return repository.TouchLastUsedAsync(id, lastUsedAt, ct);
        }

        public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            if (takeRevocationFailure())
            {
                throw new InvalidOperationException("The revocation of the refresh tokens failed.");
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var token in await database.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct))
            {
                token.RevokedAt = now;
            }

            await database.SaveChangesAsync(ct);
        }
    }

    /// <summary>The personal access token repository of the host, with a revocation that the in-memory database runs.</summary>
    private sealed class RowByRowUserPatRepository(MeisterProPRDbContext database, UserPatRepository repository)
        : IUserPatRepository
    {
        public Task AddAsync(UserPat pat, CancellationToken ct = default)
        {
            return repository.AddAsync(pat, ct);
        }

        public Task<UserPat?> GetActiveByRawTokenAsync(string rawToken, CancellationToken ct = default)
        {
            return repository.GetActiveByRawTokenAsync(rawToken, ct);
        }

        public Task<IReadOnlyList<UserPat>> ListForUserAsync(Guid userId, CancellationToken ct = default)
        {
            return repository.ListForUserAsync(userId, ct);
        }

        public Task RevokeAsync(Guid patId, Guid userId, CancellationToken ct = default)
        {
            return repository.RevokeAsync(patId, userId, ct);
        }

        public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            foreach (var pat in await database.UserPats.Where(p => p.UserId == userId && !p.IsRevoked).ToListAsync(ct))
            {
                pat.IsRevoked = true;
            }

            await database.SaveChangesAsync(ct);
        }
    }
}
