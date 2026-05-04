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
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.IdentityAndAccess;

public sealed class IdentityAndAccessModuleIntegrationTests(IdentityAndAccessModuleIntegrationTests.IdentityAndAccessApiFactory factory)
    : IClassFixture<IdentityAndAccessModuleIntegrationTests.IdentityAndAccessApiFactory>
{
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
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());

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
