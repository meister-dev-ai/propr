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
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.ReviewArchive;

/// <summary>
///     End-to-end coverage for the admin review-archive read API over a real HTTP pipeline. The host is
///     wired with the real <see cref="RetainedPullRequestDataController" /> and the real
///     <see cref="ReviewArchiveStore" /> over an in-memory EF context; only the SCM/AI externals and auth
///     identity resolution are faked. Seeded archive data is read back through the three GET endpoints.
/// </summary>
public sealed class ReviewArchiveReadApiIntegrationTests(ReviewArchiveReadApiIntegrationTests.ReviewArchiveApiFactory factory)
    : IClassFixture<ReviewArchiveReadApiIntegrationTests.ReviewArchiveApiFactory>
{
    [Fact]
    public async Task GetRetainedThreads_ReturnsThreadsWithAuthorshipAndStatus()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-threads";
        const long pullRequestId = 11;

        await factory.WithStoreAsync(async store =>
        {
            var key = new PullRequestRetentionKey(clientId, connectionId, repositoryId, pullRequestId);
            await store.UpsertThreadAsync(
                key,
                new RetainedThreadSnapshot(
                    "thread-1",
                    "src/Foo.cs",
                    21,
                    "active",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    [
                        new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow.AddMinutes(-3), "Human comment"),
                        new RetainedCommentSnapshot("c2", "propr-bot", true, DateTimeOffset.UtcNow.AddMinutes(-2), "AI reply"),
                    ]));
        });

        var route =
            $"/clients/{clientId}/review-archive/pull-requests/threads?repositoryId={repositoryId}&pullRequestId={pullRequestId}";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);

        var thread = Assert.Single(body.RootElement.EnumerateArray().ToList());
        Assert.Equal("thread-1", thread.GetProperty("threadId").GetString());
        Assert.Equal("src/Foo.cs", thread.GetProperty("filePath").GetString());
        Assert.Equal(21, thread.GetProperty("line").GetInt32());
        Assert.Equal("active", thread.GetProperty("status").GetString());

        var comments = thread.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(2, comments.Count);

        var human = comments.Single(c => c.GetProperty("commentId").GetString() == "c1");
        Assert.Equal("alice", human.GetProperty("authorIdentity").GetString());
        Assert.False(human.GetProperty("isAiAuthored").GetBoolean());
        Assert.Equal("Human comment", human.GetProperty("body").GetString());

        var ai = comments.Single(c => c.GetProperty("commentId").GetString() == "c2");
        Assert.True(ai.GetProperty("isAiAuthored").GetBoolean());
        Assert.Equal("AI reply", ai.GetProperty("body").GetString());
    }

    [Fact]
    public async Task GetRetainedThreads_WhenNothingRetained_ReturnsEmptyArray()
    {
        var route =
            $"/clients/{Guid.NewGuid()}/review-archive/pull-requests/threads?repositoryId=repo-empty&pullRequestId=999";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Empty(body.RootElement.EnumerateArray().ToList());
    }

    [Fact]
    public async Task GetRetainedFiles_ReturnsNewestRevisionPerFile_WithoutDiffText()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-files";
        const long pullRequestId = 12;

        await factory.WithStoreAsync(async store =>
        {
            var key = new PullRequestRetentionKey(clientId, connectionId, repositoryId, pullRequestId);
            await store.SaveFileDiffsAsync(
                key,
                "rev-1",
                [
                    new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, "@@ a-old @@"),
                    new RetainedFileDiffSnapshot("assets/logo.png", "Added", true, string.Empty),
                ]);
            await store.SaveFileDiffsAsync(
                key,
                "rev-2",
                [new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, "@@ a-new @@")]);
        });

        var route =
            $"/clients/{clientId}/review-archive/pull-requests/files?repositoryId={repositoryId}&pullRequestId={pullRequestId}";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var files = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        Assert.Equal(2, files.Count);

        var sourceFile = files.Single(f => f.GetProperty("filePath").GetString() == "src/A.cs");
        Assert.Equal("rev-2", sourceFile.GetProperty("revisionKey").GetString());
        Assert.False(sourceFile.GetProperty("isBinary").GetBoolean());
        // The file listing endpoint never carries the diff text.
        Assert.False(sourceFile.TryGetProperty("unifiedDiff", out _));

        var binaryFile = files.Single(f => f.GetProperty("filePath").GetString() == "assets/logo.png");
        Assert.Equal("rev-1", binaryFile.GetProperty("revisionKey").GetString());
        Assert.True(binaryFile.GetProperty("isBinary").GetBoolean());
    }

    [Fact]
    public async Task GetRetainedFileDiff_ReturnsStoredUnifiedDiffForNewestRevision()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-diff";
        const long pullRequestId = 13;
        const string newestDiff = "@@ -1,1 +1,2 @@\n+newest line";

        await factory.WithStoreAsync(async store =>
        {
            var key = new PullRequestRetentionKey(clientId, connectionId, repositoryId, pullRequestId);
            await store.SaveFileDiffsAsync(
                key,
                "rev-1",
                [new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, "@@ -1,1 +1,1 @@\n+old line")]);
            await store.SaveFileDiffsAsync(
                key,
                "rev-2",
                [new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, newestDiff)]);
        });

        var route =
            $"/clients/{clientId}/review-archive/pull-requests/file-diff?repositoryId={repositoryId}&pullRequestId={pullRequestId}&filePath=src/A.cs";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("src/A.cs", body.GetProperty("filePath").GetString());
        Assert.Equal("rev-2", body.GetProperty("revisionKey").GetString());
        Assert.Equal("Modified", body.GetProperty("changeType").GetString());
        Assert.False(body.GetProperty("isBinary").GetBoolean());
        Assert.Equal(newestDiff, body.GetProperty("unifiedDiff").GetString());
    }

    [Fact]
    public async Task GetRetainedFileDiff_ResolvesWithoutConnectionId_AndPicksMostRecentlyActiveConnection()
    {
        var clientId = Guid.NewGuid();
        const string repositoryId = "repo-multi-connection";
        const long pullRequestId = 14;

        // The same repository + pull request retained under two distinct connections. The read carries
        // no connection id, so the store must resolve to the most recently active retained pull request.
        await factory.WithStoreAsync(async store =>
        {
            var olderConnectionKey = new PullRequestRetentionKey(clientId, Guid.NewGuid(), repositoryId, pullRequestId);
            await store.TouchPullRequestAsync(olderConnectionKey, "open", DateTimeOffset.UtcNow.AddDays(-3));
            await store.SaveFileDiffsAsync(
                olderConnectionKey,
                "rev-1",
                [new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, "@@ older-connection @@")]);

            var newerConnectionKey = new PullRequestRetentionKey(clientId, Guid.NewGuid(), repositoryId, pullRequestId);
            await store.TouchPullRequestAsync(newerConnectionKey, "open", DateTimeOffset.UtcNow);
            await store.SaveFileDiffsAsync(
                newerConnectionKey,
                "rev-1",
                [new RetainedFileDiffSnapshot("src/A.cs", "Modified", false, "@@ newer-connection @@")]);
        });

        var route =
            $"/clients/{clientId}/review-archive/pull-requests/file-diff?repositoryId={repositoryId}&pullRequestId={pullRequestId}&filePath=src/A.cs";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("@@ newer-connection @@", body.GetProperty("unifiedDiff").GetString());
    }

    [Fact]
    public async Task GetRetainedFileDiff_WhenNoDiffRetained_ReturnsNotFound()
    {
        var route =
            $"/clients/{Guid.NewGuid()}/review-archive/pull-requests/file-diff?repositoryId=repo-missing&pullRequestId=404&filePath=src/Missing.cs";

        var response = await factory.SendAdminGetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRetainedThreads_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var route =
            $"/clients/{Guid.NewGuid()}/review-archive/pull-requests/threads?repositoryId=repo&pullRequestId=1";

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class ReviewArchiveApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-review-archive-jwt-secret32!";
        private const string ValidAdminKey = "admin-key-min-16-chars-ok";
        private readonly string _dbName = $"TestDb_ReviewArchive_{Guid.NewGuid()}";
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

        /// <summary>Runs an action against a scoped real <see cref="IReviewArchiveStore" /> for seeding.</summary>
        public async Task WithStoreAsync(Func<IReviewArchiveStore, Task> action)
        {
            using var scope = this.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IReviewArchiveStore>();
            await action(store);
        }

        /// <summary>Issues an admin-authenticated GET request through the real HTTP pipeline.</summary>
        public async Task<HttpResponseMessage> SendAdminGetAsync(string route)
        {
            var client = this.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.GenerateAdminToken());
            return await client.SendAsync(request);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                // In-memory EF Core context. AddDbContextFactory also exposes a scoped MeisterProPRDbContext,
                // which the review-archive store resolves below.
                services.AddDbContextFactory<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped(sp =>
                    sp.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>().CreateDbContext());

                // The review-archive module is only registered when a real DB connection string is present,
                // so wire the real store explicitly over the in-memory context for this read-API test.
                services.AddScoped<IReviewArchiveStore, ReviewArchiveStore>();

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
