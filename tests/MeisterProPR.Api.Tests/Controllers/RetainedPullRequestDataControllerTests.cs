// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.ReviewArchive;
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

/// <summary>Coverage for the retained pull-request data read endpoints.</summary>
public sealed class RetainedPullRequestDataControllerTests(RetainedPullRequestDataControllerTests.RetainedDataApiFactory factory)
    : IClassFixture<RetainedPullRequestDataControllerTests.RetainedDataApiFactory>
{
    private const string RepositoryId = "octo/my-repo";
    private const long PullRequestId = 17;
    private static readonly Guid OriginatingJobId = Guid.NewGuid();

    [Fact]
    public async Task GetRetainedThreads_AdminJwt_ReturnsMappedThreads()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/threads"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        var thread = Assert.Single(body.EnumerateArray());
        Assert.Equal("thread-1", thread.GetProperty("threadId").GetString());
        var comment = Assert.Single(thread.GetProperty("comments").EnumerateArray());
        Assert.Equal("Looks good", comment.GetProperty("body").GetString());
        Assert.True(comment.GetProperty("isAiAuthored").GetBoolean());
        Assert.Equal(OriginatingJobId, comment.GetProperty("originatingJobId").GetGuid());
    }

    [Fact]
    public async Task GetRetainedFiles_AdminJwt_ReturnsMappedFiles()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/files"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var file = Assert.Single(body.EnumerateArray());
        Assert.Equal("src/A.cs", file.GetProperty("filePath").GetString());
        Assert.Equal("rev-2", file.GetProperty("revisionKey").GetString());
    }

    [Fact]
    public async Task GetRetainedFileDiff_AdminJwt_ReturnsStoredDiff()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/file-diff"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}"
            + "&filePath=src/A.cs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("src/A.cs", body.GetProperty("filePath").GetString());
        Assert.Equal("@@ stored @@", body.GetProperty("unifiedDiff").GetString());
    }

    [Fact]
    public async Task GetRetainedFileDiff_WhenNoStoredDiff_Returns404()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/file-diff"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}"
            + "&filePath=src/Missing.cs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRetainedThreads_NoCredentials_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/threads"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRetainedThreads_ClientUserForOwningClient_Returns200()
    {
        // A non-admin caller with read (ClientUser) access to the owning client may read retained data —
        // the endpoint is no longer admin-only.
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/threads"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRetainedThreads_AuthenticatedNonMemberOfClient_Returns403()
    {
        // An authenticated user with no role on the owning client is still denied — read access is scoped
        // per client, so it is not open to any authenticated caller.
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/review-archive/pull-requests/threads"
            + $"?repositoryId={Uri.EscapeDataString(RepositoryId)}&pullRequestId={PullRequestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateNonMemberToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class RetainedDataApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-retained-data-jwt-secret-32!";

        public Guid ClientId { get; } = Guid.NewGuid();

        public Guid ClientUserId { get; } = Guid.NewGuid();

        public Guid NonMemberUserId { get; } = Guid.NewGuid();

        public string GenerateAdminToken()
        {
            return GenerateToken(Guid.NewGuid(), AppUserRole.Admin);
        }

        // A non-admin user who holds ClientUser (read) access on the owning client.
        public string GenerateClientUserToken()
        {
            return GenerateToken(this.ClientUserId, AppUserRole.User);
        }

        // A non-admin user with no role on the owning client.
        public string GenerateNonMemberToken()
        {
            return GenerateToken(this.NonMemberUserId, AppUserRole.User);
        }

        private static string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
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
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(this.ClientUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<AppUser?>(
                            new AppUser
                            {
                                Id = this.ClientUserId,
                                Username = "client.user",
                                GlobalRole = AppUserRole.User,
                                IsActive = true,
                                CreatedAt = DateTimeOffset.UtcNow,
                                ClientAssignments =
                                {
                                    new UserClientRole
                                    {
                                        Id = Guid.NewGuid(),
                                        UserId = this.ClientUserId,
                                        ClientId = this.ClientId,
                                        Role = ClientRole.ClientUser,
                                        AssignedAt = DateTimeOffset.UtcNow,
                                    },
                                },
                            }));
                userRepo.GetByIdWithAssignmentsAsync(Arg.Is<Guid>(id => id != this.ClientUserId), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var store = Substitute.For<IReviewArchiveStore>();

                store.GetThreadsForPullRequestAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string>(),
                        Arg.Any<long>(),
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<IReadOnlyList<RetainedThreadView>>(
                        [
                            new RetainedThreadView(
                                "thread-1",
                                "src/A.cs",
                                12,
                                "active",
                                DateTimeOffset.UtcNow,
                                [
                                    new RetainedCommentView(
                                        "c1",
                                        "propr-bot",
                                        true,
                                        DateTimeOffset.UtcNow,
                                        "Looks good",
                                        OriginatingJobId),
                                ]),
                        ]));

                store.ListRetainedFilesForPullRequestAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string>(),
                        Arg.Any<long>(),
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<IReadOnlyList<RetainedFileSummaryView>>(
                        [
                            new RetainedFileSummaryView("src/A.cs", "rev-2", "edit", false, DateTimeOffset.UtcNow),
                        ]));

                store.GetFileDiffAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string>(),
                        Arg.Any<long>(),
                        Arg.Any<string?>(),
                        "src/A.cs",
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<RetainedFileDiffView?>(
                            new RetainedFileDiffView(
                                "rev-2",
                                "src/A.cs",
                                "edit",
                                false,
                                "@@ stored @@",
                                DateTimeOffset.UtcNow)));

                store.GetFileDiffAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string>(),
                        Arg.Any<long>(),
                        Arg.Any<string?>(),
                        "src/Missing.cs",
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<RetainedFileDiffView?>(null));

                services.AddScoped(_ => store);
            });
        }
    }
}
