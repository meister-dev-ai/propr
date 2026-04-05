// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Middleware;

/// <summary>Integration tests for authentication middleware (previously AdminKeyMiddleware, now AuthMiddleware).</summary>
public sealed class AdminKeyMiddlewareTests(AdminKeyMiddlewareTests.AdminKeyFactory factory)
    : IClassFixture<AdminKeyMiddlewareTests.AdminKeyFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

    [Fact]
    public async Task GetJobs_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // NOTE: GetJobs_WithValidAdminKey_Returns200 was removed in US3 — the X-Admin-Key
    // legacy bypass has been deleted. Use JWT or PAT for authentication instead.

    [Fact]
    public async Task GetJobs_WithWrongAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", "wrong-key-value");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class AdminKeyFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", "test-admin-jwt-secret-32chars!!");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                var jobRepo = Substitute.For<IJobRepository>();
                jobRepo.GetAllJobsAsync(
                        Arg.Any<int>(), Arg.Any<int>(),
                        Arg.Any<MeisterProPR.Domain.Enums.JobStatus?>(),
                        Arg.Any<Guid?>(), Arg.Any<int?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<(int, IReadOnlyList<MeisterProPR.Domain.Entities.ReviewJob>)>((0, [])));
                jobRepo.GetProcessingJobsAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Domain.Entities.ReviewJob>>([]));
                services.AddSingleton(jobRepo);

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<MeisterProPR.Domain.Entities.AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, MeisterProPR.Domain.Enums.ClientRole>()));
                services.AddSingleton(userRepo);
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());
            });
        }
    }
}
