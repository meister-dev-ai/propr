// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Middleware;

/// <summary>
///     Failing tests for T010: AuthMiddleware populates ClientRoles on JWT path.
///     T011: ClientAdministrator gets 200 on their client's AI connections.
///     T012: ClientAdministrator gets 403 on another client's AI connections.
/// </summary>
public sealed class AuthMiddlewareClientRolesTests(AuthMiddlewareClientRolesTests.ClientRolesFactory factory)
    : IClassFixture<AuthMiddlewareClientRolesTests.ClientRolesFactory>
{
    /// <summary>
    ///     T010: After JWT authentication, the middleware populates context.Items["ClientRoles"]
    ///     with the user's client role assignments, allowing ClientAdministrator-gated endpoints
    ///     to return 200 instead of 401/403.
    ///     Fails until AdminKeyMiddleware calls GetUserClientRolesAsync and stores the result.
    /// </summary>
    [Fact]
    public async Task AuthMiddleware_WithJwt_PopulatesClientRolesInContext_AllowsClientAdminAccess()
    {
        var token = factory.GenerateUserToken(factory.TestUserId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.OwnedClientId}/ai-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        // Fails until middleware populates ClientRoles and controller checks them
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     T011: A user with ClientAdministrator role for OwnedClientId can list AI connections.
    ///     Fails until middleware+controller enforce ClientRoles.
    /// </summary>
    [Fact]
    public async Task ClientAdministrator_CanAccess_AiConnectionsForAssignedClient_Returns200()
    {
        var token = factory.GenerateUserToken(factory.TestUserId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.OwnedClientId}/ai-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     T012: A user with ClientAdministrator role for OwnedClientId is denied access to
    ///     another unrelated client's AI connections.
    ///     Fails until middleware+controller enforce ClientRoles.
    /// </summary>
    [Fact]
    public async Task ClientAdministrator_CannotAccess_AiConnectionsForOtherClient_Returns403()
    {
        var token = factory.GenerateUserToken(factory.TestUserId);
        var http = factory.CreateClient();
        // UnownedClientId is a different client — user has no role for it
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.UnownedClientId}/ai-connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClientAdministrator_CanAccess_PromptOverridesForAssignedClient_Returns200()
    {
        var token = factory.GenerateUserToken(factory.TestUserId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.OwnedClientId}/prompt-overrides");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientAdministrator_CannotAccess_PromptOverridesForOtherClient_Returns403()
    {
        var token = factory.GenerateUserToken(factory.TestUserId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.UnownedClientId}/prompt-overrides");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─── Factory ─────────────────────────────────────────────────────────────────

    public sealed class ClientRolesFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-client-roles-tests-abc123";
        private const string ValidAdminKey = "admin-key-min-16-chars-ok";

        /// <summary>Client the test user is a ClientAdministrator for.</summary>
        public Guid OwnedClientId { get; } = Guid.NewGuid();

        /// <summary>A different client the test user has NO role for.</summary>
        public Guid UnownedClientId { get; } = Guid.NewGuid();

        /// <summary>The test user's ID.</summary>
        public Guid TestUserId { get; } = Guid.NewGuid();

        /// <summary>Generates a JWT Bearer token for the given user (globalRole=User).</summary>
        public string GenerateUserToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                    new[]
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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var ownedClientId = this.OwnedClientId;
            var testUserId = this.TestUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                // IUserRepository: GetUserClientRolesAsync returns {ownedClientId: ClientAdministrator}
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetUserClientRolesAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { ownedClientId, ClientRole.ClientAdministrator },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != testUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                services.AddSingleton(userRepo);

                // IAiConnectionRepository: return empty list (we just test auth, not data)
                var aiRepo = Substitute.For<IAiConnectionRepository>();
                aiRepo.GetByClientAsync(ownedClientId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AiConnectionDto>>([]));
                aiRepo.GetByClientAsync(
                        Arg.Is<Guid>(id => id != ownedClientId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AiConnectionDto>>([]));
                services.AddSingleton(aiRepo);

                services.AddSingleton(Substitute.For<IClientRegistry>());

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var promptOverrideService = Substitute.For<IPromptOverrideService>();
                promptOverrideService.ListByClientAsync(ownedClientId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<PromptOverrideDto>>([]));
                promptOverrideService.ListByClientAsync(
                        Arg.Is<Guid>(id => id != ownedClientId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<PromptOverrideDto>>([]));
                services.AddSingleton(promptOverrideService);

                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }
    }
}
