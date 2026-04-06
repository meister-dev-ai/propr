// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

/// <summary>
///     Integration tests verifying that the application can start without legacy
///     environment variables that are being removed in US3.
/// </summary>
public sealed class StartupTests : IClassFixture<StartupTests.NoAiEndpointFactory>
{
    private readonly NoAiEndpointFactory _factory;

    public StartupTests(NoAiEndpointFactory factory)
    {
        this._factory = factory;
    }

    /// <summary>
    ///     T026 [US3] — Verifies the application starts successfully without <c>AI_ENDPOINT</c>
    ///     set in configuration. After US3 the global AI endpoint is removed and AI connections
    ///     are sourced per-client from the database.
    ///
    ///     RED state: startup currently throws <see cref="InvalidOperationException" />
    ///     because <c>RequireConfig("AI_ENDPOINT")</c> runs in <c>Program.cs</c> and
    ///     <c>InfrastructureServiceExtensions</c> also throws when <c>AI_ENDPOINT</c> is null.
    ///
    ///     GREEN state: after T030 + T033 remove both places that require <c>AI_ENDPOINT</c>,
    ///     the application starts and <c>GET /healthz</c> responds with 200 or 503.
    /// </summary>
    [Fact]
    public async Task Application_StartsWithoutAiEndpointEnvVar_HealthCheckPasses()
    {
        var client = this._factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
            $"Expected 200 or 503 but got {(int)response.StatusCode} {response.StatusCode}");
    }

    // ─── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Factory that deliberately omits <c>AI_ENDPOINT</c> to prove the app no longer
    ///     requires it after US3. All external service dependencies are stubbed.
    /// </summary>
    public sealed class NoAiEndpointFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");

            // Set JWT secret so JwtTokenService can be registered
            builder.UseSetting("MEISTER_JWT_SECRET", "test-startup-jwt-secret-32chars!!");

            // Deliberately NO AI_ENDPOINT / AI_DEPLOYMENT — that is the point of this test.
            // Currently startup throws InvalidOperationException here.
            // After T030 + T033, startup succeeds without these env vars.

            builder.ConfigureServices(services =>
            {
                // Stub all services that require live external connections
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                // Stub repositories so the app can resolve services without a real DB.
                // These stubs also satisfy worker startup DI resolution after T031+T032,
                // when InMemoryJobRepository/EnvVarClientRegistry are deleted.
                var jobRepo = Substitute.For<IJobRepository>();
                jobRepo.GetProcessingJobsAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Domain.Entities.ReviewJob>>([]));
                services.AddSingleton(jobRepo);

                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<MeisterProPR.Domain.Entities.AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, MeisterProPR.Domain.Enums.ClientRole>()));
                services.AddSingleton(userRepo);

                var aiConnectionRepo = Substitute.For<IAiConnectionRepository>();
                aiConnectionRepo.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Application.DTOs.AiConnectionDto>>([]));
                services.AddSingleton(aiConnectionRepo);
            });
        }
    }
}
