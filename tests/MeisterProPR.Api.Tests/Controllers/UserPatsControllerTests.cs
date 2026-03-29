using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>Integration tests for <see cref="MeisterProPR.Api.Controllers.UserPatsController"/>.</summary>
public sealed class UserPatsControllerTests(UserPatsControllerTests.UserPatsApiFactory factory)
    : IClassFixture<UserPatsControllerTests.UserPatsApiFactory>
{
    [Fact]
    public async Task GetPats_RevokedPatsAbsentActivePresent_Returns200WithOnlyActive()
    {
        var userId = factory.UserId;
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.UserPats.AddRange(
                new UserPatRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TokenHash = "active-hash",
                    Label = "Active PAT",
                    IsRevoked = false,
                    CreatedAt = now,
                },
                new UserPatRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TokenHash = "revoked-hash",
                    Label = "Revoked PAT",
                    IsRevoked = true,
                    CreatedAt = now.AddMinutes(-10),
                });
            await db.SaveChangesAsync();
        }

        var token = factory.GenerateUserToken(userId);
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/me/pats");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        // Only the active PAT should be present
        var labels = items.EnumerateArray().Select(e => e.GetProperty("label").GetString()).ToList();
        Assert.Contains("Active PAT", labels);
        Assert.DoesNotContain("Revoked PAT", labels);
    }

    [Fact]
    public async Task GetPats_NoActivePats_Returns200WithEmptyArray()
    {
        var userId = factory.UserId;
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            // Seed only a revoked PAT
            db.UserPats.Add(new UserPatRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "revoked-only",
                Label = "Revoked Only",
                IsRevoked = true,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var token = factory.GenerateUserToken(userId);
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/me/pats");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public async Task GetPats_WithoutAuth_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/me/pats");
        // No auth header

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class UserPatsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-min-32-chars-long!!";
        private readonly string _dbName = $"TestDb_UserPats_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid UserId { get; } = Guid.NewGuid();

        public string GenerateUserToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", "User"),
                }),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-client-key-1234567890");
            builder.UseSetting("MEISTER_ADMIN_KEY", "admin-key-min-16-chars-ok");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                services.AddScoped<IUserPatRepository, UserPatRepository>();

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<Application.DTOs.CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var clientRegistry = Substitute.For<IClientRegistry>();
                clientRegistry.IsValidKey(Arg.Any<string>()).Returns(false);
                clientRegistry.GetClientIdByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(null));
                services.AddSingleton(clientRegistry);
            });
        }
    }
}
