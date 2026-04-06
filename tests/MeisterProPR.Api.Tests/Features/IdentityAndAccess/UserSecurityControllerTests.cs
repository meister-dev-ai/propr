// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.IdentityAndAccess;

public sealed class UserSecurityControllerTests(UserSecurityControllerTests.UserSecurityApiFactory factory)
    : IClassFixture<UserSecurityControllerTests.UserSecurityApiFactory>
{
    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_Returns204_UpdatesHashAndRevokesRefreshTokens()
    {
        var userId = await this.SeedUserAsync("alice", "OldPassword1!");
        await factory.RefreshTokens.AddAsync(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = "token-hash-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/users/me/password");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(userId));
        request.Content = JsonContent.Create(new
        {
            currentPassword = "OldPassword1!",
            newPassword = "NewPassword2!",
        });

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var passwordHashService = scope.ServiceProvider.GetRequiredService<IPasswordHashService>();
        var user = await userRepository.GetByIdAsync(userId);
        Assert.NotNull(user);
        Assert.True(passwordHashService.Verify("NewPassword2!", user!.PasswordHash));
        Assert.False(passwordHashService.Verify("OldPassword1!", user.PasswordHash));
        Assert.All(factory.RefreshTokens.Tokens.Where(t => t.UserId == userId), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_Returns401()
    {
        var userId = await this.SeedUserAsync("bob", "CorrectPassword1!");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/users/me/password");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(userId));
        request.Content = JsonContent.Create(new
        {
            currentPassword = "WrongPassword1!",
            newPassword = "NewPassword2!",
        });

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithShortPassword_Returns400()
    {
        var userId = await this.SeedUserAsync("charlie", "CorrectPassword1!");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/users/me/password");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(userId));
        request.Content = JsonContent.Create(new
        {
            currentPassword = "CorrectPassword1!",
            newPassword = "short",
        });

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithoutAuthentication_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/users/me/password");
        request.Content = JsonContent.Create(new
        {
            currentPassword = "CorrectPassword1!",
            newPassword = "NewPassword2!",
        });

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<Guid> SeedUserAsync(string username, string password)
    {
        using var scope = factory.Services.CreateScope();
        var passwordHashService = scope.ServiceProvider.GetRequiredService<IPasswordHashService>();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHashService.Hash(password),
            GlobalRole = AppUserRole.User,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await userRepository.AddAsync(user);
        return user.Id;
    }

    public sealed class UserSecurityApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-user-password-jwt-secret-32chars!!";
        private readonly string _dbName = $"TestDb_UserPassword_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public InMemoryRefreshTokenRepository RefreshTokens { get; } = new();

        public string GenerateUserToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", "User"),
                    new Claim(JwtRegisteredClaimNames.UniqueName, "test-user"),
                ]),
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
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddSingleton<IPasswordHashService, PasswordHashService>();
                services.AddSingleton<IRefreshTokenRepository>(this.RefreshTokens);

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IPrStatusFetcher>());
                services.AddSingleton(Substitute.For<IThreadMemoryService>());
                services.AddSingleton(Substitute.For<IClientAdoCredentialRepository>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                services.AddScoped<IUserRepository, AppUserRepository>();

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<Application.DTOs.CrawlConfigurationDto>>([]));
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
    }

    public sealed class InMemoryRefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly List<RefreshToken> _tokens = [];

        public IReadOnlyList<RefreshToken> Tokens => this._tokens;

        public Task AddAsync(RefreshToken token, CancellationToken ct = default)
        {
            this._tokens.Add(token);
            return Task.CompletedTask;
        }

        public Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(this._tokens.FirstOrDefault(t =>
                t.TokenHash == tokenHash &&
                t.RevokedAt is null &&
                t.ExpiresAt > now));
        }

        public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var token in this._tokens.Where(t => t.UserId == userId && t.RevokedAt is null))
            {
                token.RevokedAt = now;
            }

            return Task.CompletedTask;
        }
    }
}
