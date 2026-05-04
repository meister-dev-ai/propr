// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
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

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     Shared API test fixture for the tenant-auth feature slice. It centralizes the minimal host
///     bootstrap, common identity constants, and JWT generation so the tenant administration and
///     sign-in tests can focus on behavior instead of repeating startup plumbing.
/// </summary>
public class TenantAuthTestFixture : WebApplicationFactory<Program>
{
    private const string TestJwtSecret = "test-tenant-auth-jwt-secret-32chars";
    private const string ValidAdminKey = "tenant-auth-admin-key-min-16";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid TenantAdministratorUserId { get; } = Guid.NewGuid();
    public Guid TenantUserId { get; } = Guid.NewGuid();
    public Guid PlatformAdministratorUserId { get; } = Guid.NewGuid();
    public Guid SsoProviderId { get; } = Guid.NewGuid();
    public string TenantSlug { get; } = "acme";
    public string OtherTenantSlug { get; } = "globex";

    public string GenerateTenantAdministratorToken()
    {
        return this.GenerateToken(this.TenantAdministratorUserId, AppUserRole.User);
    }

    public string GenerateTenantUserToken()
    {
        return this.GenerateToken(this.TenantUserId, AppUserRole.User);
    }

    public string GeneratePlatformAdministratorToken()
    {
        return this.GenerateToken(this.PlatformAdministratorUserId, AppUserRole.Admin);
    }

    protected virtual IReadOnlyDictionary<Guid, ClientRole> ResolveClientRoles(Guid userId)
    {
        return new Dictionary<Guid, ClientRole>();
    }

    protected virtual void ConfigureTenantAuthServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
        builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
        builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
        builder.UseSetting("MEISTER_CLIENT_KEYS", "placeholder-client-key-x");
        builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
        builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddSingleton(Substitute.For<IPullRequestFetcher>());
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

            var jobRepo = Substitute.For<IJobRepository>();
            jobRepo.GetAllJobsAsync(
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<JobStatus?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<int?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<(int, IReadOnlyList<ReviewJob>)>((0, [])));
            jobRepo.GetProcessingJobsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
            services.AddSingleton(jobRepo);

            var userRepo = Substitute.For<IUserRepository>();
            userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<AppUser?>(null));
            userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var userId = callInfo.ArgAt<Guid>(0);
                    var roles = new Dictionary<Guid, ClientRole>(this.ResolveClientRoles(userId));
                    return Task.FromResult<IReadOnlyDictionary<Guid, ClientRole>>(roles)
                        .ContinueWith(task => new Dictionary<Guid, ClientRole>(task.Result));
                });
            services.AddSingleton(userRepo);

            services.AddSingleton(Substitute.For<IClientRegistry>());
            services.AddSingleton(Substitute.For<IThreadMemoryRepository>());

            this.ConfigureTenantAuthServices(services);
        });
    }

    private string GenerateToken(Guid userId, AppUserRole globalRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", globalRole.ToString()),
                }),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Issuer = "meisterpropr",
            Audience = "meisterpropr",
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
