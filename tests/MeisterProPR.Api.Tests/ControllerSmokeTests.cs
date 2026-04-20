// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

/// <summary>
///     DI-wiring smoke tests that verify controller endpoints respond successfully,
///     catching failures that /healthz alone would not surface (e.g., missing logger
///     registrations or broken controller dependency graphs).
/// </summary>
public sealed class ControllerSmokeTests(ControllerSmokeTests.SmokeFactory factory)
    : IClassFixture<ControllerSmokeTests.SmokeFactory>
{
    private const string ValidAdminKey = "smoke-admin-key-min-16-chars";

    /// <summary>
    ///     Verifies that GET /clients resolves the full controller DI graph (including
    ///     <see cref="Microsoft.Extensions.Logging.ILogger{T}" />) and returns 200.
    ///     A broken dependency — such as a missing logger — causes a 500 rather than
    ///     a startup failure, so this test catches regressions that /healthz cannot.
    /// </summary>
    [Fact]
    public async Task GetClients_WithAdminKey_Returns200()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     Verifies that GET /clients rejects requests missing the admin key,
    ///     confirming authentication middleware is active on controller endpoints.
    /// </summary>
    [Fact]
    public async Task GetClients_WithoutAdminKey_Returns401()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    ///     <see cref="WebApplicationFactory{TEntryPoint}" /> configured for smoke testing.
    ///     Uses an isolated in-memory database and stubs all external dependencies so the
    ///     full DI graph can be resolved without external services.
    /// </summary>
    public sealed class SmokeFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-smoke-jwt-secret-32chars!!!";
        private readonly string _dbName = $"TestDb_Smoke_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public string GenerateAdminToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("global_role", "Admin"),
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
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                services.AddSingleton(crawlRepo);

                services.AddSingleton(Substitute.For<IClientRegistry>());

                services.AddSingleton(Substitute.For<IClientAdoOrganizationScopeRepository>());
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
}
