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
using MeisterProPR.Domain.ValueObjects;
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

public sealed class ReviewsControllerPostTests(ReviewsControllerPostTests.ReviewsApiFactory factory)
    : IClassFixture<ReviewsControllerPostTests.ReviewsApiFactory>
{
    private HttpRequestMessage CreateValidRequest(Guid clientId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{clientId}/reviewing/jobs");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = JsonContent.Create(
            new
            {
                provider = "azureDevOps",
                hostBaseUrl = "https://dev.azure.com/myorg",
                repository = new
                {
                    externalRepositoryId = "my-repo",
                    ownerOrNamespace = "my-project",
                    projectPath = "my-project",
                },
                codeReview = new
                {
                    platform = "pullRequest",
                    externalReviewId = "42",
                    number = 42,
                },
                reviewRevision = new
                {
                    headSha = "head-sha",
                    baseSha = "base-sha",
                    startSha = "base-sha",
                    providerRevisionId = "1",
                    patchIdentity = "base-sha...head-sha",
                },
            });
        return request;
    }

    [Fact]
    public async Task PostReviews_LegacyAdoShape_Returns400()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{factory.ClientId}/reviewing/jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.ClientAdministratorUserId));
        request.Content = JsonContent.Create(
            new
            {
                provider = "azureDevOps",
                hostBaseUrl = "https://dev.azure.com/myorg",
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "my-project",
                repositoryId = "my-repo",
                pullRequestId = 42,
                iterationId = 1,
            });
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostReviews_WithoutCredentials_Returns401()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = this.CreateValidRequest(factory.ClientId, null);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostReviews_ClientUserRole_Returns403()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = this.CreateValidRequest(
            factory.ClientId,
            factory.GenerateUserToken(factory.ClientUserUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostReviews_SamePrIterationSubmittedTwice_Returns409WithSameJobId()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var adminToken = factory.GenerateUserToken(factory.ClientAdministratorUserId);

        using var firstRequest = this.CreateValidRequest(factory.ClientId, adminToken);
        var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync()).RootElement;
        var firstJobId = firstBody.GetProperty("jobId").GetGuid();

        using var secondRequest = this.CreateValidRequest(factory.ClientId, adminToken);
        var secondResponse = await client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync()).RootElement;
        var secondJobId = secondBody.GetProperty("jobId").GetGuid();

        Assert.Equal(firstJobId, secondJobId);
        Assert.Equal("pending", secondBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostReviews_ValidRequest_Returns202WithJobIdAndPendingStatus()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = this.CreateValidRequest(
            factory.ClientId,
            factory.GenerateUserToken(factory.ClientAdministratorUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(Guid.Empty, body.GetProperty("jobId").GetGuid());
        Assert.Equal("pending", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostReviews_PrFetcherReturnsData_JobReceivesPrContextViaRepository()
    {
        await using var prContextFactory = new ReviewsApiPrContextFactory();
        await prContextFactory.ClearJobsAsync();
        var client = prContextFactory.CreateClient();

        using var request = this.CreateValidRequest(
            prContextFactory.ClientId,
            prContextFactory.GenerateUserToken(prContextFactory.ClientAdministratorUserId));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var jobId = body.GetProperty("jobId").GetGuid();
        var job = await prContextFactory.GetJobAsync(jobId);

        Assert.NotNull(job);
        Assert.Equal("Add feature X", job!.PrTitle);
        Assert.Equal("main", job.PrTargetBranch);
    }

    [Fact]
    public async Task PostReviews_PrFetcherThrows_JobStillCreatedWith202()
    {
        await using var throwingFactory = new ReviewsApiPrFetcherThrowsFactory();
        await throwingFactory.ClearJobsAsync();
        var client = throwingFactory.CreateClient();

        using var request = this.CreateValidRequest(
            throwingFactory.ClientId,
            throwingFactory.GenerateUserToken(throwingFactory.ClientAdministratorUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(Guid.Empty, body.GetProperty("jobId").GetGuid());
    }

    public class ReviewsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-reviews-post-jwt-secret-32!!";

        private readonly string _dbName = $"TestDb_ReviewsApiFactory_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();
        public Guid ClientUserUserId { get; } = Guid.NewGuid();

        public async Task ClearJobsAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ReviewJobs.RemoveRange(db.ReviewJobs);
            await db.SaveChangesAsync();
        }

        public async Task<ReviewJob?> GetJobAsync(Guid jobId)
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            return await repository.GetByIdWithFileResultsAsync(jobId);
        }

        public string GenerateUserToken(Guid userId)
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
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var clientUserUserId = this.ClientUserUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

                ReplaceService(services, this.CreatePullRequestFetcher());
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
                userRepo.GetUserClientRolesAsync(clientUserUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientUser },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId && id != clientUserUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);
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
                    DisplayName = "Review Trigger Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        protected virtual IPullRequestFetcher CreatePullRequestFetcher()
        {
            return Substitute.For<IPullRequestFetcher>();
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

    internal sealed class ReviewsApiPrContextFactory : ReviewsApiFactory
    {
        protected override IPullRequestFetcher CreatePullRequestFetcher()
        {
            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new PullRequest(
                            "https://dev.azure.com/myorg",
                            "my-project",
                            "my-repo",
                            "my-repo",
                            77,
                            1,
                            "Add feature X",
                            null,
                            "feature/add-x",
                            "main",
                            [])));
            return prFetcher;
        }
    }

    internal sealed class ReviewsApiPrFetcherThrowsFactory : ReviewsApiFactory
    {
        protected override IPullRequestFetcher CreatePullRequestFetcher()
        {
            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns<Task<PullRequest>>(_ => throw new InvalidOperationException("ADO unavailable"));
            return prFetcher;
        }
    }
}
