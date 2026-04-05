// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Middleware;

/// <summary>
///     Integration tests for the refactored <c>AuthMiddleware</c> (US3).
///     Verifies that the legacy <c>X-Admin-Key</c> header bypass has been fully removed.
/// </summary>
public sealed class AuthMiddlewareTests(AuthMiddlewareTests.AuthMiddlewareFactory factory)
    : IClassFixture<AuthMiddlewareTests.AuthMiddlewareFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

    /// <summary>
    ///     T027 [US3] — Asserts that a request carrying only an <c>X-Admin-Key</c> header
    ///     is no longer granted admin access and receives 401 Unauthorized.
    ///
    ///     RED state: <c>AdminKeyMiddleware</c> still reads <c>MEISTER_ADMIN_KEY</c> and
    ///     grants access when the key matches, returning 200 OK.
    ///
    ///     GREEN state: after T028 creates <c>AuthMiddleware</c> without the
    ///     <c>X-Admin-Key</c> branch, the header is ignored and 401 is returned.
    /// </summary>
    [Fact]
    public async Task XAdminKeyHeader_IsIgnoredAfterRemoval_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        // Send only the legacy X-Admin-Key header — should be ignored after US3
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);

        // After T028, X-Admin-Key carries no authority → 401 Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    public sealed class AuthMiddlewareFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "placeholder-client-key-x");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", "test-middleware-jwt-secret-xyz!!");

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
