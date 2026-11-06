// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.IdentityAndAccess;

public sealed class IdentityAndAccessModuleIntegrationTests(IdentityAndAccessModuleIntegrationTests.IdentityAndAccessApiFactory factory)
    : IClassFixture<IdentityAndAccessModuleIntegrationTests.IdentityAndAccessApiFactory>
{
    private const string LensIssuer = "https://propr.test/api";
    private const string LensCallback = "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr";

    private const string LoginRequiredCallback = LensCallback + "?state=deployment-diagnostic&iss=https%3A%2F%2Fpropr.test%2Fapi&error=login_required";

    // The address ranges that an Azure Container Apps environment with workload profiles reserves for the platform.
    private const string ContainerAppsProxyNetworks = "100.100.0.0/17,100.100.128.0/19,100.100.160.0/19,100.100.192.0/19";

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsAccessAndRefreshTokens()
    {
        await factory.ResetStateAsync();
        await factory.SeedUserAsync("alice", "CorrectPassword1!");

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new
            {
                username = "alice",
                password = "CorrectPassword1!",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        // Refresh token is delivered only as an httpOnly cookie, never in the JSON body.
        Assert.False(body.TryGetProperty("refreshToken", out _));
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("meisterpropr_refresh=", StringComparison.Ordinal)
                      && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.Single(db.RefreshTokens);
    }

    [Fact]
    public async Task Refresh_WithValidRefreshToken_ReturnsNewAccessToken()
    {
        await factory.ResetStateAsync();
        var user = await factory.SeedUserAsync("bob", "CorrectPassword1!");
        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await factory.SeedRefreshTokenAsync(user.Id, rawRefreshToken);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/auth/refresh",
            new
            {
                refreshToken = rawRefreshToken,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
    }

    [Fact]
    public async Task Login_AfterConsecutiveFailures_LocksAccountEvenForCorrectPassword()
    {
        await factory.ResetStateAsync();
        await factory.SeedUserAsync("dave", "CorrectPassword1!");
        var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failure = await client.PostAsJsonAsync(
                "/auth/login",
                new { username = "dave", password = "WrongPassword1!" });
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { username = "dave", password = "CorrectPassword1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("account_locked", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_Successful_ResetsFailureCounter()
    {
        await factory.ResetStateAsync();
        var user = await factory.SeedUserAsync("erin", "CorrectPassword1!");
        var client = factory.CreateClient();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await client.PostAsJsonAsync("/auth/login", new { username = "erin", password = "WrongPassword1!" });
        }

        var ok = await client.PostAsJsonAsync("/auth/login", new { username = "erin", password = "CorrectPassword1!" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = db.AppUsers.Single(u => u.Id == user.Id);
        Assert.Equal(0, record.FailedLoginAttempts);
        Assert.Null(record.LockoutEndAt);
    }

    [Fact]
    public async Task Login_ExceedingRateLimit_Returns429()
    {
        await factory.ResetStateAsync();
        using var limited = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_ENABLED", "true");
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_PERMITS", "3");
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_WINDOW_SECONDS", "60");
        });
        var client = limited.CreateClient();

        var last = HttpStatusCode.OK;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/auth/login",
                new { username = "nobody", password = "whatever" });
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task ExtensionSignIn_CountsAgainstAWindowOfItsOwn()
    {
        await factory.ResetStateAsync();
        using var limited = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_ENABLED", "true");
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_PERMITS", "3");
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_EXTENSION_PERMITS", "5");
            builder.UseSetting("MEISTER_AUTH_RATELIMIT_WINDOW_SECONDS", "60");
        });
        var client = limited.CreateClient();
        var exchange = new
        {
            clientId = "propr-lens",
            redirectUri = LensCallback,
            code = "code",
            codeVerifier = new string('v', 43),
            grantType = "authorization_code",
        };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/extension/token", exchange)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/auth/extension/token", exchange)).StatusCode);
        var login = await client.PostAsJsonAsync("/auth/login", new { username = "nobody", password = "whatever" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task ExtensionToken_ReachesOnlyTheExtensionActions()
    {
        await factory.ResetStateAsync();
        var access = Substitute.For<IExtensionAuthorizationService>();
        using var lens = this.WithExtensionSignIn(access);
        var user = await factory.SeedUserAsync("lens-user", "CorrectPassword1!");
        var grantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        access.IsAccessActiveAsync(user.Id, grantId, sessionId, Arg.Any<CancellationToken>()).Returns(true);
        var token = lens.Services.GetRequiredService<IJwtTokenService>().GenerateExtensionAccessToken(user, grantId, sessionId);
        var client = lens.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/auth/me", token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(client, HttpMethod.Post, "/users/me/pats", token, new { label = "created-by-extension" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, "/auth/refresh", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/auth/extension/grants", token)).StatusCode);

        access.IsAccessActiveAsync(user.Id, grantId, sessionId, Arg.Any<CancellationToken>()).Returns(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/auth/me", token)).StatusCode);
    }

    [Fact]
    public async Task ExtensionToken_OfADisabledAccount_IsRefusedAsDisabled()
    {
        await factory.ResetStateAsync();
        var access = Substitute.For<IExtensionAuthorizationService>();
        access.IsAccessActiveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        using var lens = this.WithExtensionSignIn(access);
        var user = await factory.SeedUserAsync("disabled-lens-user", "CorrectPassword1!");
        var token = lens.Services.GetRequiredService<IJwtTokenService>().GenerateExtensionAccessToken(user, Guid.NewGuid(), Guid.NewGuid());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            (await db.AppUsers.SingleAsync(record => record.Id == user.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await SendAsync(lens.CreateClient(), HttpMethod.Get, "/auth/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("User account is not active.", body.GetProperty("error").GetString());
    }

    // The API serves plain http, so the test server receives every request over http, as the API does behind a proxy that
    // terminates TLS. The connection address is the address of the proxy.
    [Theory]
    [InlineData(ContainerAppsProxyNetworks, "100.100.0.206")]
    [InlineData(ContainerAppsProxyNetworks, "::ffff:100.100.0.206")]
    [InlineData(null, "10.1.2.3")]
    public async Task ExtensionAuthorization_ThatATrustedProxyReportsAsHttps_AnswersWithTheCallback(string? trustedProxyNetworks, string proxyAddress)
    {
        await factory.ResetStateAsync();
        var access = Substitute.For<IExtensionAuthorizationService>();
        access.StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionAuthorizationStart(RedirectUrl: LoginRequiredCallback));
        using var lens = this.WithExtensionSignIn(access, trustedProxyNetworks);

        var context = await lens.Server.SendAsync(http => Authorize(http, proxyAddress, "https"));

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(LoginRequiredCallback, context.Response.Headers.Location.ToString());
        await access.Received(1).StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    // Without the setting, ProPR does not trust the platform range of Azure Container Apps.
    [InlineData(null, "100.100.0.206", "https")]
    // A client outside the trusted networks sends the header itself.
    [InlineData(ContainerAppsProxyNetworks, "203.0.113.10", "https")]
    // The trusted proxy reached ProPR over http.
    [InlineData(ContainerAppsProxyNetworks, "100.100.0.206", "http")]
    // The request carries no forwarding headers.
    [InlineData(ContainerAppsProxyNetworks, "100.100.0.206", null)]
    public async Task ExtensionAuthorization_ThatNoTrustedProxyReportsAsHttps_IsRefusedWithHttpsRequired(
        string? trustedProxyNetworks, string proxyAddress, string? forwardedProto)
    {
        await factory.ResetStateAsync();
        var access = Substitute.For<IExtensionAuthorizationService>();
        using var lens = this.WithExtensionSignIn(access, trustedProxyNetworks);

        var context = await lens.Server.SendAsync(http => Authorize(http, proxyAddress, forwardedProto));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("https_required", body.RootElement.GetProperty("error").GetString());
        await access.DidNotReceive().StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("100.100.0.206", StatusCodes.Status200OK)]
    [InlineData("203.0.113.10", StatusCodes.Status400BadRequest)]
    public async Task ExtensionTokenExchange_RunsOnlyForARequestThatATrustedProxyReportsAsHttps(string proxyAddress, int expectedStatus)
    {
        await factory.ResetStateAsync();
        var access = Substitute.For<IExtensionAuthorizationService>();
        access.ExchangeAsync(Arg.Any<ExtensionTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionTokenDto("extension-access", "Bearer", 300, Guid.NewGuid().ToString(), LensIssuer));
        using var lens = this.WithExtensionSignIn(access, ContainerAppsProxyNetworks);
        var exchange = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                clientId = "propr-lens",
                redirectUri = LensCallback,
                code = "code",
                codeVerifier = new string('v', 43),
                grantType = "authorization_code",
            });

        var context = await lens.Server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Post;
            http.Request.Path = "/auth/extension/token";
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = exchange.Length;
            http.Request.Body = new MemoryStream(exchange);
            ForwardThroughProxy(http, proxyAddress, "https");
        });

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        await access.Received(expectedStatus == StatusCodes.Status200OK ? 1 : 0)
            .ExchangeAsync(Arg.Any<ExtensionTokenRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMe_WithJwtAndClientAssignments_ReturnsRoleMap()
    {
        await factory.ResetStateAsync();
        var user = await factory.SeedUserAsync("carol", "CorrectPassword1!");
        var clientId = Guid.NewGuid();
        await factory.SeedClientRoleAsync(user.Id, clientId, ClientRole.ClientAdministrator);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(user.Id, AppUserRole.User));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("User", body.GetProperty("globalRole").GetString());
        var clientRoles = body.GetProperty("clientRoles");
        Assert.Equal((int)ClientRole.ClientAdministrator, clientRoles.GetProperty(clientId.ToString()).GetInt32());
        Assert.True(body.GetProperty("hasLocalPassword").GetBoolean());
    }

    [Fact]
    public async Task CreatePat_ThenRevoke_RemovesPatFromList()
    {
        await factory.ResetStateAsync();
        var user = await factory.SeedUserAsync("dave", "CorrectPassword1!");

        var client = factory.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/users/me/pats");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(user.Id, AppUserRole.User));
        createRequest.Content = JsonContent.Create(
            new
            {
                label = "Integration PAT",
                expiresAt = (DateTimeOffset?)null,
            });

        var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        var patId = createdBody.GetProperty("id").GetGuid();
        var token = createdBody.GetProperty("token").GetString();
        Assert.NotNull(token);
        Assert.StartsWith("mpr_", token, StringComparison.Ordinal);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/users/me/pats");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(user.Id, AppUserRole.User));
        var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .ToList();
        Assert.Single(listed);
        Assert.Equal("Integration PAT", listed[0].GetProperty("label").GetString());

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"/users/me/pats/{patId}");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(user.Id, AppUserRole.User));
        var revokeResponse = await client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        using var secondListRequest = new HttpRequestMessage(HttpMethod.Get, "/users/me/pats");
        secondListRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(user.Id, AppUserRole.User));
        var secondListResponse = await client.SendAsync(secondListRequest);
        var secondListed = JsonDocument.Parse(await secondListResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, secondListed.GetArrayLength());
    }

    [Fact]
    public async Task AdminUsers_PrefixedRoutes_CreateAndListUsers()
    {
        await factory.ResetStateAsync();

        var client = factory.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/users");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), AppUserRole.Admin));
        createRequest.Content = JsonContent.Create(
            new
            {
                username = "erin",
                password = "CorrectPassword1!",
            });

        var createResponse = await client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/identity/users");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), AppUserRole.Admin));

        var listResponse = await client.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .ToList();
        Assert.Contains(
            listed,
            item => string.Equals(item.GetProperty("username").GetString(), "erin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminUsers_PrefixedRoutes_AssignClientRoleAndExposeItInDetail()
    {
        await factory.ResetStateAsync();
        var user = await factory.SeedUserAsync("frank", "CorrectPassword1!");
        var clientId = Guid.NewGuid();
        await factory.SeedClientAsync(clientId);

        var client = factory.CreateClient();

        using var assignRequest = new HttpRequestMessage(HttpMethod.Post, $"/admin/identity/users/{user.Id}/clients");
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), AppUserRole.Admin));
        assignRequest.Content = JsonContent.Create(
            new
            {
                clientId,
                role = (int)ClientRole.ClientAdministrator,
            });

        var assignResponse = await client.SendAsync(assignRequest);

        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/identity/users/{user.Id}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), AppUserRole.Admin));

        var detailResponse = await client.SendAsync(detailRequest);

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync()).RootElement;
        var assignments = detail.GetProperty("assignments").EnumerateArray().ToList();
        Assert.Contains(assignments, assignment => assignment.GetProperty("clientId").GetGuid() == clientId);
    }

    private WebApplicationFactory<Program> WithExtensionSignIn(IExtensionAuthorizationService access, string? trustedProxyNetworks = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            if (trustedProxyNetworks is not null)
            {
                builder.UseSetting("MEISTER_TRUSTED_PROXY_NETWORKS", trustedProxyNetworks);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ExtensionAuthorizationOptions>();
                services.AddSingleton(new ExtensionAuthorizationOptions(LensIssuer, [LensCallback]));
                services.RemoveAll<IExtensionAuthorizationService>();
                services.AddSingleton(access);
            });
        });
    }

    private static void Authorize(HttpContext http, string proxyAddress, string? forwardedProto)
    {
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/auth/extension/authorize";
        http.Request.QueryString = QueryString.Create(
            new Dictionary<string, string?>
            {
                ["client_id"] = "propr-lens",
                ["redirect_uri"] = LensCallback,
                ["response_type"] = "code",
                ["code_challenge"] = new string('a', 43),
                ["code_challenge_method"] = "S256",
                ["state"] = "deployment-diagnostic",
                ["prompt"] = "none",
            });
        ForwardThroughProxy(http, proxyAddress, forwardedProto);
    }

    private static void ForwardThroughProxy(HttpContext http, string proxyAddress, string? forwardedProto)
    {
        http.Connection.RemoteIpAddress = IPAddress.Parse(proxyAddress);
        if (forwardedProto is not null)
        {
            http.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            http.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public sealed class IdentityAndAccessApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-identity-access-jwt-secret-32!!";
        private readonly string _dbName = $"TestDb_IdentityAndAccess_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public string GenerateUserToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
                    new Claim(JwtRegisteredClaimNames.UniqueName, "test-user"),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };

            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        public async Task ResetStateAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.UserClientRoles.RemoveRange(db.UserClientRoles);
            db.UserPats.RemoveRange(db.UserPats);
            db.RefreshTokens.RemoveRange(db.RefreshTokens);
            db.AppUsers.RemoveRange(db.AppUsers);
            db.Clients.RemoveRange(db.Clients);
            await db.SaveChangesAsync();
        }

        public async Task<AppUser> SeedUserAsync(string username, string password, AppUserRole role = AppUserRole.User)
        {
            using var scope = this.Services.CreateScope();
            var passwordHashService = scope.ServiceProvider.GetRequiredService<IPasswordHashService>();
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            var user = new AppUser
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = passwordHashService.Hash(password),
                GlobalRole = role,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await userRepository.AddAsync(user);
            return user;
        }

        public async Task SeedClientRoleAsync(Guid userId, Guid clientId, ClientRole role)
        {
            await this.SeedClientAsync(clientId);

            using var scope = this.Services.CreateScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            await userRepository.AddClientAssignmentAsync(
                new UserClientRole
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    ClientId = clientId,
                    Role = role,
                    AssignedAt = DateTimeOffset.UtcNow,
                });
        }

        public async Task SeedClientAsync(Guid clientId)
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            if (await db.Clients.AnyAsync(record => record.Id == clientId))
            {
                return;
            }

            db.Clients.Add(
                new ClientRecord
                {
                    Id = clientId,
                    DisplayName = "Identity Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        public async Task SeedRefreshTokenAsync(Guid userId, string rawRefreshToken)
        {
            using var scope = this.Services.CreateScope();
            var refreshTokens = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
            await refreshTokens.AddAsync(
                new RefreshToken
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TokenHash = ComputeSha256(rawRefreshToken),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddSingleton<IPasswordHashService, PasswordHashService>();

                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));

                services.AddScoped<IUserRepository, AppUserRepository>();
                services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
                services.AddScoped<IUserPatRepository, UserPatRepository>();
                services.AddScoped<IAccountLockoutService, AccountLockoutService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IPrStatusFetcher>());
                services.AddSingleton(Substitute.For<IThreadMemoryService>());
                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Database.EnsureCreated();

            return host;
        }

        private static string ComputeSha256(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
