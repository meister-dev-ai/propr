// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>Integration tests for the enable/disable flow on <c>AdminUsersController</c>.</summary>
public sealed class AdminUsersControllerTests(AdminUsersControllerTests.AdminUsersApiFactory factory)
    : IClassFixture<AdminUsersControllerTests.AdminUsersApiFactory>
{
    private static AppUser User(Guid id, bool isActive, AppUserRole role = AppUserRole.User, string username = "target")
    {
        return new AppUser { Id = id, Username = username, GlobalRole = role, IsActive = isActive };
    }

    private void ResetSubstitutes()
    {
        factory.UserRepository.ClearReceivedCalls();
        factory.RefreshTokenRepository.ClearReceivedCalls();
        factory.UserPatRepository.ClearReceivedCalls();
        factory.AuditLog.ClearReceivedCalls();
    }

    private HttpRequestMessage PatchRequest(Guid id, bool isActive, string role = "Admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/users/{id}")
        {
            Content = JsonContent.Create(new { isActive }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), role));
        return request;
    }

    private HttpRequestMessage DeleteRequest(Guid id, string role = "Admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/users/{id}/permanent");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), role));
        return request;
    }

    [Fact]
    public async Task Patch_DisablesActiveUser_Returns204AndRevokesAndAudits()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, true));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(3);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).SetActiveAsync(id, false, Arg.Any<CancellationToken>());
        await factory.RefreshTokenRepository.Received(1).RevokeAllForUserAsync(id, Arg.Any<CancellationToken>());
        await factory.UserPatRepository.Received(1).RevokeAllForUserAsync(id, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Disabled(Arg.Any<Guid>(), id, "target");
        factory.AuditLog.DidNotReceive().Reenabled(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Patch_ReenablesDisabledUser_Returns204AndRevokesAndAudits()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, false));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).SetActiveAsync(id, true, Arg.Any<CancellationToken>());
        await factory.RefreshTokenRepository.Received(1).RevokeAllForUserAsync(id, Arg.Any<CancellationToken>());
        await factory.UserPatRepository.Received(1).RevokeAllForUserAsync(id, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Reenabled(Arg.Any<Guid>(), id, "target");
    }

    [Fact]
    public async Task Patch_ReenableAlreadyActive_IsNoOp()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, true));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.DidNotReceive().SetActiveAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await factory.RefreshTokenRepository.DidNotReceive().RevokeAllForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        factory.AuditLog.DidNotReceive().Reenabled(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
        factory.AuditLog.DidNotReceive().Disabled(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Patch_DisableAlreadyDisabled_IsNoOp()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, false));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.DidNotReceive().SetActiveAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await factory.UserPatRepository.DidNotReceive().RevokeAllForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        factory.AuditLog.DidNotReceive().Disabled(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Patch_DisableLastActiveAdmin_Returns409AndAuditsBlockedAndDoesNotMutate()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, true, AppUserRole.Admin, "lastadmin"));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Cannot disable the last active global admin.", body.GetProperty("error").GetString());

        factory.AuditLog.Received(1).DisableBlockedByLastAdmin(Arg.Any<Guid>(), id, "lastadmin");
        factory.AuditLog.DidNotReceive().Disabled(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
        await factory.UserRepository.DidNotReceive().SetActiveAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Patch_DisableAdmin_WhenAnotherActiveAdminRemains_Succeeds()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, true, AppUserRole.Admin, "admin2"));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(2);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).SetActiveAsync(id, false, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Disabled(Arg.Any<Guid>(), id, "admin2");
    }

    [Fact]
    public async Task Patch_ReenableAdmin_IsNeverBlockedByLastAdminGuard()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, false, AppUserRole.Admin, "admin3"));
        // Even with zero currently-active admins, re-enabling can only add one, so it must not be blocked.
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(0);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).SetActiveAsync(id, true, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Reenabled(Arg.Any<Guid>(), id, "admin3");
    }

    [Fact]
    public async Task Patch_UnknownUser_Returns404()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<AppUser?>(null));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_NonAdminCaller_Returns403()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, true));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.PatchRequest(id, false, "User"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Patch_NoCredentials_Returns401()
    {
        this.ResetSubstitutes();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/users/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { isActive = false }),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JwtPath_DisabledUser_Returns401NotActive()
    {
        this.ResetSubstitutes();
        var callerId = Guid.NewGuid();
        factory.UserRepository.GetByIdWithAssignmentsAsync(callerId, Arg.Any<CancellationToken>())
            .Returns(User(callerId, false, AppUserRole.Admin));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(callerId, "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("User account is not active.", body.GetProperty("error").GetString());

        // Reset the caller stub so other tests keep their default (null) identity resolution.
        factory.UserRepository.GetByIdWithAssignmentsAsync(callerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AppUser?>(null));
    }

    [Fact]
    public async Task Delete_NonAdminUser_Returns204AndDeletesAndAudits()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, true, AppUserRole.User, "victim"));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Deleted(Arg.Any<Guid>(), id, "victim");
    }

    [Fact]
    public async Task Delete_DisabledAdmin_IsNotBlockedByLastAdminGuard()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        // A disabled admin does not count toward active admins, so deleting one is always allowed.
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, false, AppUserRole.Admin, "olddmin"));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_Admin_WhenAnotherActiveAdminRemains_Succeeds()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, true, AppUserRole.Admin, "admin2"));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(2);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.UserRepository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        factory.AuditLog.Received(1).Deleted(Arg.Any<Guid>(), id, "admin2");
    }

    [Fact]
    public async Task Delete_LastActiveAdmin_Returns409AndAuditsBlockedAndDoesNotDelete()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(User(id, true, AppUserRole.Admin, "lastadmin"));
        factory.UserRepository.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Cannot delete the last active administrator.", body.GetProperty("error").GetString());

        factory.AuditLog.Received(1).DeleteBlockedByLastAdmin(Arg.Any<Guid>(), id, "lastadmin");
        factory.AuditLog.DidNotReceive().Deleted(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>());
        await factory.UserRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_UnknownUser_Returns404()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<AppUser?>(null));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory.UserRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NonAdminCaller_Returns403()
    {
        this.ResetSubstitutes();
        var id = Guid.NewGuid();
        factory.UserRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(User(id, true));

        var client = factory.CreateClient();
        var response = await client.SendAsync(this.DeleteRequest(id, "User"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class AdminUsersApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-for-integration-tests-abc123";

        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();

        public IRefreshTokenRepository RefreshTokenRepository { get; } = Substitute.For<IRefreshTokenRepository>();

        public IUserPatRepository UserPatRepository { get; } = Substitute.For<IUserPatRepository>();

        public IUserAccountAuditLog AuditLog { get; } = Substitute.For<IUserAccountAuditLog>();

        public string GenerateUserToken(Guid userId, string globalRole = "User")
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                    new[]
                    {
                        new Claim("sub", userId.ToString()),
                        new Claim("global_role", globalRole),
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
            builder.UseSetting("DB_CONNECTION_STRING", string.Empty);
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-client-key-placeholder");
            builder.UseSetting("MEISTER_ADMIN_KEY", "admin-key-min-16-chars-ok");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());

                // Caller identity resolution defaults to null so the JWT admin claim is authoritative;
                // individual tests override this for the target user and the not-active path.
                this.UserRepository.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));

                services.AddSingleton(this.UserRepository);
                services.AddSingleton(this.RefreshTokenRepository);
                services.AddSingleton(this.UserPatRepository);
                services.AddSingleton(this.AuditLog);
            });
        }
    }
}
