// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
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

/// <summary>Integration tests for <see cref="MeisterProPR.Api.Controllers.JobsController" />.</summary>
public sealed class JobsControllerTests(JobsControllerTests.JobsApiFactory factory)
    : IClassFixture<JobsControllerTests.JobsApiFactory>
{
    [Fact]
    public async Task GetJobs_DefaultPagination_Returns200WithTotalAndItems()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs?limit=10&offset=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("total", out _));
        Assert.True(body.RootElement.TryGetProperty("items", out _));
    }


    [Fact]
    public async Task GetJobs_ResponseItems_DoNotExposeRawClientKey()
    {
        // S1 fix: ensure the raw client key string is never in the response
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("clientKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-key-123", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetJobs_StatusFilter_ReturnsOnlyMatchingJobs()
    {
        var repo = factory.Services.GetRequiredService<IJobRepository>();
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            500,
            1);
        await repo.AddAsync(completedJob);
        await repo.TryTransitionAsync(completedJob.Id, JobStatus.Pending, JobStatus.Processing);
        await repo.SetResultAsync(completedJob.Id, new ReviewResult("done", []));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs?status=Completed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal("completed", item.GetProperty("status").GetString());
        }
    }


    /// <summary>
    ///     Verifies GET /reviewing/jobs?limit=100 responds in under 2 seconds even with 10,000 seeded jobs.
    ///     The test bulk-seeds through the EF Core in-memory provider so the timing covers the read path,
    ///     not 10,000 individual repository writes.
    /// </summary>
    [Fact]
    public async Task GetJobs_With10kJobs_RespondsUnder2Seconds()
    {
        using var scope = factory.Services.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var jobs = new List<ReviewJob>(10_000);
        for (var i = 0; i < 10_000; i++)
        {
            jobs.Add(
                new ReviewJob(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    i + 1,
                    1));
        }

        await dbContext.ReviewJobs.AddRangeAsync(jobs);
        await dbContext.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs?limit=100&offset=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var sw = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"GET /reviewing/jobs?limit=100 took {sw.Elapsed.TotalMilliseconds:F0}ms — expected < 2000ms");
    }

    [Fact]
    public async Task GetJobs_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task GetJobs_WithValidAdminKey_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_WithWrongAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviewing/jobs");
        request.Headers.Add("X-Admin-Key", "wrong-key-here");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetJobProtocol_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        var jobId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{jobId}/protocol");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetJobProtocol_ForNonExistentJob_Returns404()
    {
        var client = factory.CreateClient();
        var jobId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{jobId}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetJobProtocol_ForJobWithoutProtocol_Returns404()
    {
        // Seed a job without a protocol
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 999, 1);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetJobProtocol_ForJobWithProtocol_Returns200WithProtocolData()
    {
        // Seed a job with a protocol (using the in-memory repo's direct access)
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 888, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
            TotalInputTokens = 1500L,
            TotalOutputTokens = 750L,
            IterationCount = 2,
            ToolCallCount = 3,
            FinalConfidence = 90,
        };
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var firstProtocol = body.RootElement.EnumerateArray().First();
        Assert.Equal(protocol.Id.ToString(), firstProtocol.GetProperty("id").GetString());
        Assert.Equal(job.Id.ToString(), firstProtocol.GetProperty("jobId").GetString());
        Assert.Equal(1500L, firstProtocol.GetProperty("totalInputTokens").GetInt64());
        Assert.Equal(750L, firstProtocol.GetProperty("totalOutputTokens").GetInt64());
        Assert.Equal("Completed", firstProtocol.GetProperty("outcome").GetString());
    }

    public sealed class JobsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jobs-api-jwt-secret-32chars!";
        private readonly string _dbName = $"TestDb_Jobs_{Guid.NewGuid()}";
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                // InMemory EF Core DB + IJobRepository (replaces deleted InMemoryJobRepository)
                services.AddDbContextFactory<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

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
///     T021 [US2]: ClientUser can access review jobs for their assigned client.
///     Ensures non-admin JWT users with a ClientUser role get 200 on GET /reviewing/jobs.
/// </summary>
public sealed class JobsJwtTests(JobsJwtTests.JobsJwtApiFactory factory)
    : IClassFixture<JobsJwtTests.JobsJwtApiFactory>
{
    /// <summary>
    ///     T021 [US2]: A ClientUser should be able to access review jobs (200).
    ///     Verifies that JWT-authenticated non-admin users are not blocked from listing jobs.
    /// </summary>
    [Fact]
    public async Task ClientUser_CanAccess_ReviewJobs_ForAssignedClient_Returns200()
    {
        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/reviewing/jobs?clientId={factory.AssignedClientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientUser_CanAccess_JobDetail_ForAssignedClient_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(
            Guid.NewGuid(),
            factory.AssignedClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            123,
            1);
        await jobRepo.AddAsync(job);

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task JobDetail_IncludesSnapshottedAiSettings()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(
            Guid.NewGuid(),
            factory.AssignedClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1234,
            1);
        job.SetAiConfig(Guid.NewGuid(), "gpt-4.1", 0.35f);
        await jobRepo.AddAsync(job);

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("gpt-4.1", body.RootElement.GetProperty("aiModel").GetString());
        Assert.Equal(0.35m, body.RootElement.GetProperty("reviewTemperature").GetDecimal());
    }

    [Fact]
    public async Task ClientUser_CanAccess_JobResult_ForAssignedClient_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(
            Guid.NewGuid(),
            factory.AssignedClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            124,
            1);
        await jobRepo.AddAsync(job);
        await jobRepo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await jobRepo.SetResultAsync(job.Id, new ReviewResult("summary", []));

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/result");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientUser_CanAccess_JobProtocol_ForAssignedClient_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(
            Guid.NewGuid(),
            factory.AssignedClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            125,
            1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                Label = "src/Foo.cs",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = "Completed",
            });
        await jobRepo.AddAsync(job);

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientUser_CanAccess_PrView_ForAssignedClient_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(
            Guid.NewGuid(),
            factory.AssignedClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            126,
            1);
        await jobRepo.AddAsync(job);

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.AssignedClientId}/reviewing/pr-view?providerScopePath=https://dev.azure.com/org&providerProjectKey=proj&repositoryId=repo&pullRequestId=126");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientUser_CannotAccess_JobProtocol_ForUnassignedClient_Returns403()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 127, 1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                Label = "src/Bar.cs",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = "Completed",
            });
        await jobRepo.AddAsync(job);

        var token = factory.GenerateClientUserToken(factory.TestUserId, factory.AssignedClientId);
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class JobsJwtApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-jobs-tests-xyz789!";
        private readonly string _dbName = $"TestDb_JobsJwt_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        /// <summary>The test user's ID.</summary>
        public Guid TestUserId { get; } = Guid.NewGuid();

        /// <summary>Client the test user is assigned to (ClientUser role).</summary>
        public Guid AssignedClientId { get; } = Guid.NewGuid();

        /// <summary>Generates a JWT for a non-admin user.</summary>
        public string GenerateClientUserToken(Guid userId, Guid assignedClientId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", "User"),
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

            var assignedClientId = this.AssignedClientId;
            var testUserId = this.TestUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                var dbName = this._dbName;
                var dbRoot = this._dbRoot;
                services.AddDbContextFactory<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                // IUserRepository: test user has ClientUser role for AssignedClientId
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetUserClientRolesAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { assignedClientId, ClientRole.ClientUser },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != testUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                services.AddSingleton(userRepo);

                var memoryRepository = Substitute.For<IThreadMemoryRepository>();
                memoryRepository.GetPagedAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string?>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<MemorySource?>(),
                        Arg.Any<string?>(),
                        Arg.Any<int?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(call => Task.FromResult(
                        new PagedResult<ThreadMemoryRecord>(
                            [],
                            0,
                            call.ArgAt<int>(2),
                            call.ArgAt<int>(3))));
                services.AddSingleton(memoryRepository);
            });
        }
    }
}
