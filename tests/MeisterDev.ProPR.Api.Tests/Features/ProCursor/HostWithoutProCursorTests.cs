// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.ProCursor;

/// <summary>
///     The ProCursor endpoints and the session of a host that runs without ProCursor, as a hosted cell does. The host sets
///     none of the ProCursor settings, so it composes the disabled ProCursor gateway and registers no ProCursor usage
///     reporting.
/// </summary>
public sealed class HostWithoutProCursorTests(HostWithoutProCursorTests.HostWithoutProCursorFactory factory)
    : IClassFixture<HostWithoutProCursorTests.HostWithoutProCursorFactory>
{
    private const string SourceId = "6f1d8a52-0c3e-4b7a-9f21-3d5e8c4b2a10";
    private const string BranchId = "a3c9e1f4-7b2d-4e6a-8c05-1f9d2b7e4c63";

    /// <summary>The endpoints below <c>/admin/clients/{clientId}/procursor</c>, each with a valid request body where it takes one.</summary>
    public static TheoryData<string, string, string?> ClientEndpoints => new()
    {
        { "GET", "token-usage?from=2026-11-01&to=2026-11-07", null },
        { "GET", "token-usage/top-sources?period=30d&limit=5", null },
        { "GET", $"sources/{SourceId}/token-usage?period=30d", null },
        { "GET", $"sources/{SourceId}/token-usage/events?limit=10", null },
        { "GET", "token-usage/export?from=2026-11-01&to=2026-11-07", null },
        { "GET", "token-usage/freshness", null },
        { "POST", "token-usage/rebuild", """{"from":"2026-11-01","to":"2026-11-07"}""" },
        { "GET", "sources", null },
        {
            "POST", "sources",
            """
            {
              "displayName": "Platform Docs",
              "sourceKind": "repository",
              "providerProjectKey": "project-a",
              "repositoryId": "repo-a",
              "defaultBranch": "main",
              "symbolMode": "auto",
              "trackedBranches": [{ "branchName": "main", "refreshTriggerMode": "branchUpdate", "miniIndexEnabled": true }]
            }
            """
        },
        { "POST", $"sources/{SourceId}/refresh", "{}" },
        { "GET", $"sources/{SourceId}/branches", null },
        { "POST", $"sources/{SourceId}/branches", """{"branchName":"release","refreshTriggerMode":"manual"}""" },
        { "PUT", $"sources/{SourceId}/branches/{BranchId}", """{"miniIndexEnabled":false}""" },
        { "DELETE", $"sources/{SourceId}/branches/{BranchId}", null },
    };

    /// <summary>The internal endpoints that the ProCursor service calls with the shared key.</summary>
    public static TheoryData<string, string> InternalEndpoints => new()
    {
        { "POST", "/internal/propr/procursor/broker/embeddings/deployment" },
        { "POST", "/internal/propr/procursor/broker/embeddings/generate" },
        { "POST", "/internal/propr/procursor/broker/scm/materialize" },
        { "POST", "/internal/propr/procursor/broker/scm/branch-head" },
        { "GET", "/internal/propr/procursor/runtime-config/enabled" },
        { "POST", $"/internal/propr/procursor/runtime-config/sources/{SourceId}/refresh" },
    };

    [Theory]
    [MemberData(nameof(ClientEndpoints))]
    public async Task ClientEndpoint_WithoutProCursor_AnswersNotConfigured(string method, string path, string? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/admin/clients/{factory.ClientId}/procursor/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(factory.ClientAdministratorUserId));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("procursor_not_configured", answer.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ClientEndpoint_WithoutProCursor_RefusesACallerWithoutARoleOnTheClient()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/top-sources?period=30d&limit=5");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(factory.UserWithoutRoleId));

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The console reads from the session whether to show the ProCursor tab and the ProCursor usage of a client.
    [Fact]
    public async Task Session_WithoutProCursor_ReportsThatProCursorIsNotConfigured()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(factory.ClientAdministratorUserId));

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(session.RootElement.GetProperty("proCursorConfigured").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(InternalEndpoints))]
    public async Task InternalEndpoint_WithoutProCursor_RefusesEverySharedKey(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-ProCursor-Key", "a-key-this-host-does-not-know");

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    ///     A ProPR host without any ProCursor setting. Its database is an in-memory database, and the client administration
    ///     and the SCM provider registry, which a host with a database registers, are added here. The client administrator
    ///     of the one client and a user without a role on it sign in with a token.
    /// </summary>
    public sealed class HostWithoutProCursorFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-host-without-procursor-jwt-32";

        private readonly string _dbName = $"TestDb_WithoutProCursor_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();

        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();

        public Guid UserWithoutRoleId { get; } = Guid.NewGuid();

        public string GenerateToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", AppUserRole.User.ToString()),
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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("DB_CONNECTION_STRING", string.Empty);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var userWithoutRoleId = this.UserWithoutRoleId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IStockQuotaGate, UnlimitedStockQuotaGate>();
                services.AddSingleton(LocalScmPolicies.CreateRuntimeSubstitute());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                // The session endpoint needs these, and a host without a database registers neither.
                services.AddSingleton(Substitute.For<IRefreshTokenRepository>());
                services.AddSingleton(Substitute.For<IAccountLockoutService>());

                var users = Substitute.For<IUserRepository>();
                users.GetByIdWithAssignmentsAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<AppUser?>(
                            new AppUser
                            {
                                Id = clientAdministratorUserId,
                                Username = "client.admin",
                                GlobalRole = AppUserRole.User,
                                IsActive = true,
                                CreatedAt = DateTimeOffset.UtcNow,
                                ClientAssignments =
                                {
                                    new UserClientRole
                                    {
                                        Id = Guid.NewGuid(),
                                        UserId = clientAdministratorUserId,
                                        ClientId = clientId,
                                        Role = ClientRole.ClientAdministrator,
                                        AssignedAt = DateTimeOffset.UtcNow,
                                    },
                                },
                            }));
                users.GetByIdWithAssignmentsAsync(userWithoutRoleId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<AppUser?>(
                            new AppUser
                            {
                                Id = userWithoutRoleId,
                                Username = "other.user",
                                GlobalRole = AppUserRole.User,
                                IsActive = true,
                                CreatedAt = DateTimeOffset.UtcNow,
                            }));
                services.AddSingleton(users);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Client without ProCursor",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
