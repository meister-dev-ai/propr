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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Startup;

/// <summary>
///     Proves the authorization default is deny: an action carrying no auth attribute and no in-code gate is
///     unreachable without an authenticated caller, and reachable once a valid JWT is presented. The probe
///     controller below stands in for a freshly-added, gate-less endpoint.
/// </summary>
public sealed class ApiAuthorizationFallbackTests(ApiAuthorizationFallbackTests.FallbackFactory factory)
    : IClassFixture<ApiAuthorizationFallbackTests.FallbackFactory>
{
    [Fact]
    public async Task UngatedAction_WithoutCredentials_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("__tests/deny-by-default");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UngatedAction_WithValidJwt_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "__tests/deny-by-default");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class FallbackFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-fallback-jwt-secret-32chars";

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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            builder.ConfigureServices(services =>
            {
                services.AddControllers()
                    .ConfigureApplicationPartManager(manager =>
                        manager.ApplicationParts.Add(new AssemblyPart(typeof(FallbackFactory).Assembly)));

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                var jobRepo = Substitute.For<IJobRepository>();
                jobRepo.GetProcessingJobsAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
                services.AddSingleton(jobRepo);

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());
            });
        }
    }
}

/// <summary>
///     Test-only endpoint with no authorization attribute and no in-code gate. It exists solely to prove the
///     global deny-by-default fallback protects an otherwise-unguarded action.
/// </summary>
[ApiController]
[Route("__tests/deny-by-default")]
public sealed class DenyByDefaultProbeController : ControllerBase
{
    /// <summary>Returns 200 for any caller the authorization layer allows through.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        return this.Ok(new { ok = true });
    }
}
