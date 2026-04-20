// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
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
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class ReviewsControllerGetTests(ReviewsControllerGetTests.GetReviewsFactory factory)
    : IClassFixture<ReviewsControllerGetTests.GetReviewsFactory>
{
    private static HttpRequestMessage CreateGetRequest(Guid jobId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{jobId}/status");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task GetReview_CompletedJob_Returns200WithResult()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var jobId = await factory.InsertCompletedJobAsync();

        using var request = CreateGetRequest(jobId, factory.GenerateUserToken(factory.ClientUserUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.TryGetProperty("result", out var resultEl));
        Assert.True(resultEl.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task GetReview_FailedJob_Returns200WithError()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var jobId = await factory.InsertFailedJobAsync();

        using var request = CreateGetRequest(jobId, factory.GenerateUserToken(factory.ClientUserUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.TryGetProperty("error", out var errorEl));
        Assert.NotNull(errorEl.GetString());
    }

    [Fact]
    public async Task GetReview_PendingJob_Returns200WithPendingStatus()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var jobId = await factory.InsertPendingJobAsync();

        using var request = CreateGetRequest(jobId, factory.GenerateUserToken(factory.ClientUserUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pending", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(jobId, body.RootElement.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task GetReview_NoCredentials_Returns401()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = CreateGetRequest(Guid.NewGuid(), null);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetReview_UnknownJobId_Returns404()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = CreateGetRequest(Guid.NewGuid(), factory.GenerateUserToken(factory.ClientUserUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class GetReviewsFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-get-reviews-jwt-secret-32!!!";
        private readonly string _dbName = $"TestDb_GetReviewsFactory_{Guid.NewGuid()}";
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

        public async Task<Guid> InsertCompletedJobAsync()
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                this.ClientId,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                200,
                1);
            await repository.AddAsync(job);
            await repository.SetResultAsync(job.Id, new ReviewResult("AI completed", Array.Empty<ReviewComment>()));
            return job.Id;
        }

        public async Task<Guid> InsertFailedJobAsync()
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                this.ClientId,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                300,
                1);
            await repository.AddAsync(job);
            await repository.SetFailedAsync(job.Id, "Something went wrong");
            return job.Id;
        }

        public async Task<Guid> InsertPendingJobAsync()
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                this.ClientId,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                400,
                1);
            await repository.AddAsync(job);
            return job.Id;
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

                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
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
