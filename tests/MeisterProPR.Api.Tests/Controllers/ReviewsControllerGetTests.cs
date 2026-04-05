// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class ReviewsControllerGetTests(ReviewsControllerGetTests.GetReviewsFactory factory)
    : IClassFixture<ReviewsControllerGetTests.GetReviewsFactory>
{
    private static HttpRequestMessage CreateGetRequest(Guid jobId, string? adoToken = "valid-ado-token")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/reviews/{jobId}");
        if (!string.IsNullOrWhiteSpace(adoToken))
        {
            request.Headers.Add("X-Ado-Token", adoToken);
        }

        return request;
    }

    [Fact]
    public async Task GetReview_CompletedJob_Returns200WithResult()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        var jobId = await factory.InsertCompletedJobAsync();

        using var request = CreateGetRequest(jobId);
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

        using var request = CreateGetRequest(jobId);
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

        using var request = CreateGetRequest(jobId);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pending", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(jobId, body.RootElement.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task GetReview_NoAdoToken_Returns401()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = CreateGetRequest(Guid.NewGuid(), adoToken: null);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetReview_UnknownJobId_Returns404()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = CreateGetRequest(Guid.NewGuid());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class GetReviewsFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"TestDb_GetReviewsFactory_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public async Task ClearJobsAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ReviewJobs.RemoveRange(db.ReviewJobs);
            await db.SaveChangesAsync();
        }

        public async Task<Guid> InsertCompletedJobAsync()
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                Guid.NewGuid(),
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
                Guid.NewGuid(),
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
                Guid.NewGuid(),
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

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

                var adoValidator = Substitute.For<IAdoTokenValidator>();
                adoValidator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(true);
                adoValidator.IsValidAsync(Arg.Is<string>(value => value != "valid-ado-token"), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(false);

                ReplaceService(services, adoValidator);
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredentialRepository = Substitute.For<IClientAdoCredentialRepository>();
                adoCredentialRepository.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredentialRepository);
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
