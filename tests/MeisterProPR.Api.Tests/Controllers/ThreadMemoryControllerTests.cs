// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for thread-memory admin endpoints.
/// Ensures read-only endpoints do not require constructing <see cref="IThreadMemoryService"/>.
/// </summary>
public sealed class ThreadMemoryControllerTests(ThreadMemoryControllerTests.ThreadMemoryApiFactory factory)
    : IClassFixture<ThreadMemoryControllerTests.ThreadMemoryApiFactory>
{
    [Fact]
    public async Task GetStoredEmbeddings_AdminJwt_WithBrokenThreadMemoryService_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/thread-memory?clientId={factory.ClientId}&page=1&pageSize=50");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActivityLog_AdminJwt_WithBrokenThreadMemoryService_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/thread-memory/activity-log?clientId={factory.ClientId}&page=1&pageSize=50");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetStoredEmbeddings_NoCredentials_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/thread-memory?clientId={factory.ClientId}&page=1&pageSize=50");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class ThreadMemoryApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-thread-memory-jwt-secret-32!";

        public Guid ClientId { get; } = Guid.NewGuid();

        public string GenerateAdminToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
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
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var clientId = this.ClientId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IJobRepository>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
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
                    .Returns(call => Task.FromResult(new PagedResult<ThreadMemoryRecord>(
                        [
                            new ThreadMemoryRecord
                            {
                                Id = Guid.NewGuid(),
                                ClientId = clientId,
                                ThreadId = 42,
                                RepositoryId = "repo",
                                PullRequestId = 7,
                                FilePath = "src/File.cs",
                                ResolutionSummary = "Resolved",
                                EmbeddingVector = [0.1f],
                                CreatedAt = DateTimeOffset.UtcNow,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            },
                        ],
                        1,
                        call.ArgAt<int>(2),
                        call.ArgAt<int>(3))));
                services.AddSingleton(memoryRepository);

                var scanRepository = Substitute.For<IReviewPrScanRepository>();
                services.AddSingleton(scanRepository);

                var activityLog = Substitute.For<IMemoryActivityLog>();
                activityLog.QueryAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<MemoryActivityLogQuery>(),
                        Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        var query = call.ArgAt<MemoryActivityLogQuery>(1);
                        return Task.FromResult(new PagedResult<MemoryActivityLogEntry>(
                            [
                                new MemoryActivityLogEntry
                                {
                                    Id = Guid.NewGuid(),
                                    ClientId = clientId,
                                    ThreadId = 42,
                                    RepositoryId = "repo",
                                    PullRequestId = 7,
                                    Action = MemoryActivityAction.Stored,
                                    PreviousStatus = null,
                                    CurrentStatus = "resolved",
                                    Reason = null,
                                    OccurredAt = DateTimeOffset.UtcNow,
                                },
                            ],
                            1,
                            query.Page,
                            query.PageSize));
                    });
                services.AddSingleton(activityLog);

                // Intentionally broken service to guard against constructor eager activation regressions.
                services.AddScoped<IThreadMemoryService>(_ => throw new InvalidOperationException("intentional-thread-memory-service-failure"));
            });
        }
    }
}
