// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class ClientsControllerReviewerTests(ClientsControllerReviewerTests.ReviewerApiFactory factory)
    : IClassFixture<ClientsControllerReviewerTests.ReviewerApiFactory>
{
    [Fact]
    public async Task GetClient_DoesNotIncludeReviewerIdField()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{factory.OtherClientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.TryGetProperty("reviewerId", out _));
    }

    [Fact]
    public async Task PutReviewerIdentity_LegacyRouteReturnsNotFound()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{factory.ClientId}/reviewer-identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { reviewerId = Guid.NewGuid() });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class ReviewerApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-reviewer-jwt-secret-32chars!";

        private readonly string _dbName = $"TestDb_Reviewer_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid OtherClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();

        public string GenerateAdminToken()
        {
            return this.GenerateToken(Guid.NewGuid(), AppUserRole.Admin);
        }

        public string GenerateClientAdministratorToken()
        {
            return this.GenerateToken(this.ClientAdministratorUserId, AppUserRole.User);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();

                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientAdministrator },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

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
            db.Clients.AddRange(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Reviewer Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new ClientRecord
                {
                    Id = this.OtherClientId,
                    DisplayName = "Other Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        private string GenerateToken(Guid userId, AppUserRole globalRole)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", globalRole.ToString()),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        private static void ReplaceService<T>(IServiceCollection services, T implementation)
            where T : class
        {
            var descriptor = services.FirstOrDefault(candidate => candidate.ServiceType == typeof(T));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(implementation);
        }
    }
}
