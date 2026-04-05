// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
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

public sealed class ReviewsControllerListTests(ReviewsControllerListTests.ListReviewsFactory factory)
    : IClassFixture<ReviewsControllerListTests.ListReviewsFactory>
{
    private HttpRequestMessage CreateListRequest(Guid clientId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}/reviews");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task ListReviews_ClientRoleScoping_JobsFromOneClientNotVisibleToAnother()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        await factory.InsertJobAsync(factory.ClientAId, 800);

        using var request = this.CreateListRequest(factory.ClientBId, factory.GenerateUserToken(factory.ClientBUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.EnumerateArray().ToList();
        Assert.DoesNotContain(items, item => item.GetProperty("pullRequestId").GetInt32() == 800);
    }

    [Fact]
    public async Task ListReviews_EmptyRepository_Returns200WithEmptyArray()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = this.CreateListRequest(factory.ClientAId, factory.GenerateUserToken(factory.ClientAUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(0, body.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ListReviews_WithoutCredentials_Returns401()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = this.CreateListRequest(factory.ClientAId, token: null);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListReviews_WithJobs_ReturnsNewestFirst()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();
        await factory.InsertJobAsync(factory.ClientAId, 701);
        await factory.InsertJobAsync(factory.ClientAId, 702);

        using var request = this.CreateListRequest(factory.ClientAId, factory.GenerateUserToken(factory.ClientAUserId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.EnumerateArray().ToList();

        Assert.True(items.Count >= 2);
        var first = items[0].GetProperty("submittedAt").GetDateTimeOffset();
        var second = items[1].GetProperty("submittedAt").GetDateTimeOffset();
        Assert.True(first >= second);
    }

    public sealed class ListReviewsFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-reviews-list-jwt-secret-32!!";

        private readonly string _dbName = $"TestDb_ListReviewsFactory_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientAId { get; } = Guid.NewGuid();
        public Guid ClientBId { get; } = Guid.NewGuid();
        public Guid ClientAUserId { get; } = Guid.NewGuid();
        public Guid ClientBUserId { get; } = Guid.NewGuid();

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
                Subject = new ClaimsIdentity([
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

        public async Task InsertJobAsync(Guid clientId, int pullRequestId)
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                clientId,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                pullRequestId,
                1);
            await repository.AddAsync(job);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientAId = this.ClientAId;
            var clientBId = this.ClientBId;
            var clientAUserId = this.ClientAUserId;
            var clientBUserId = this.ClientBUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

                ReplaceService(services, Substitute.For<IAdoTokenValidator>());
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientAUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { clientAId, ClientRole.ClientUser },
                    }));
                userRepo.GetUserClientRolesAsync(clientBUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { clientBId, ClientRole.ClientUser },
                    }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAUserId && id != clientBUserId),
                        Arg.Any<CancellationToken>())
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

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord
                {
                    Id = this.ClientAId,
                    DisplayName = "Client A",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new ClientRecord
                {
                    Id = this.ClientBId,
                    DisplayName = "Client B",
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
