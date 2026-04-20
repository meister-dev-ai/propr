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

namespace MeisterProPR.Api.Tests.Features.Reviewing.Intake;

public sealed class ReviewJobIntakeIntegrationTests(ReviewJobIntakeIntegrationTests.IntakeApiFactory factory)
    : IClassFixture<ReviewJobIntakeIntegrationTests.IntakeApiFactory>
{
    [Fact]
    public async Task SubmitReview_ValidRequest_ReturnsAcceptedAndPersistsPendingJob()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = factory.CreateSubmitRequest();
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var jobId = body.GetProperty("jobId").GetGuid();

        var persisted = await factory.GetJobAsync(jobId);
        Assert.NotNull(persisted);
        Assert.Equal(JobStatus.Pending, persisted!.Status);
        Assert.Equal("feature/add-x", persisted.PrSourceBranch);
        Assert.Equal("main", persisted.PrTargetBranch);
    }

    [Fact]
    public async Task GetReview_CompletedJob_ReturnsResultPayload()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var jobId = await factory.InsertCompletedJobAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{jobId}/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.ClientAdministratorUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal("done", body.GetProperty("result").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task SubmitReview_LegacyAzureDevOpsRequestShape_ReturnsBadRequest()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

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

    public sealed class IntakeApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-review-intake-jwt-secret-32!!";
        private readonly string _dbName = $"TestDb_ReviewIntake_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();

        public HttpRequestMessage CreateSubmitRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{this.ClientId}/reviewing/jobs");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                this.GenerateUserToken(this.ClientAdministratorUserId));
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

        public async Task<Guid> InsertCompletedJobAsync()
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                this.ClientId,
                "https://dev.azure.com/myorg",
                "my-project",
                "my-repo",
                77,
                1);
            await repository.AddAsync(job);
            await repository.SetResultAsync(job.Id, new ReviewResult("done", []));
            return job.Id;
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
            var adminUserId = this.ClientAdministratorUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

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
                        new PullRequest(
                            "https://dev.azure.com/myorg",
                            "my-project",
                            "my-repo",
                            "my-repo",
                            42,
                            1,
                            "Add feature X",
                            null,
                            "refs/heads/feature/add-x",
                            "refs/heads/main",
                            []));
                ReplaceService(services, prFetcher);
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(adminUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientAdministrator }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(value => value != adminUserId),
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
                    DisplayName = "Review Intake Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
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
