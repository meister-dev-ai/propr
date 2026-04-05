// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class IdentitiesControllerTests(IdentitiesControllerTests.IdentitiesApiFactory factory)
    : IClassFixture<IdentitiesControllerTests.IdentitiesApiFactory>
{
    [Fact]
    public async Task ResolveIdentity_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identities/resolve?orgUrl=https://dev.azure.com/org&displayName=Reviewer");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ResolveIdentity_ClientUserWithoutAdministratorRole_Returns403()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identities/resolve?orgUrl=https://dev.azure.com/org&displayName=Reviewer");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResolveIdentity_ClientAdministrator_Returns200()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identities/resolve?orgUrl=https://dev.azure.com/org&displayName=Reviewer");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientAdministratorToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResolveIdentity_Admin_Returns200()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identities/resolve?orgUrl=https://dev.azure.com/org&displayName=Reviewer");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class IdentitiesApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-identities-jwt-secret-32chars";

        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();
        public Guid ClientUserUserId { get; } = Guid.NewGuid();
        public Guid AssignedClientId { get; } = Guid.NewGuid();

        public string GenerateAdminToken()
        {
            return this.GenerateToken(Guid.NewGuid(), AppUserRole.Admin);
        }

        public string GenerateClientAdministratorToken()
        {
            return this.GenerateToken(this.ClientAdministratorUserId, AppUserRole.User);
        }

        public string GenerateClientUserToken()
        {
            return this.GenerateToken(this.ClientUserUserId, AppUserRole.User);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var clientUserUserId = this.ClientUserUserId;
            var assignedClientId = this.AssignedClientId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                ReplaceService(services, Substitute.For<IAdoTokenValidator>());
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedPrFetcher>());
                ReplaceService(services, Substitute.For<IClientRegistry>());
                ReplaceService(services, Substitute.For<IJobRepository>());
                ReplaceService(services, Substitute.For<IThreadMemoryRepository>());

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<Application.DTOs.CrawlConfigurationDto>>([]));
                ReplaceService(services, crawlRepo);

                var identityResolver = Substitute.For<IIdentityResolver>();
                identityResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ResolvedIdentity>>([
                        new ResolvedIdentity(Guid.NewGuid(), "Reviewer"),
                    ]));
                ReplaceService(services, identityResolver);

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { assignedClientId, ClientRole.ClientAdministrator },
                    }));
                userRepo.GetUserClientRolesAsync(clientUserUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { assignedClientId, ClientRole.ClientUser },
                    }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId && id != clientUserUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                ReplaceService(services, userRepo);
            });
        }

        private string GenerateToken(Guid userId, AppUserRole globalRole)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
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
