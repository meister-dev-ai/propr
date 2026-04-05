// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

/// <summary>
///     Verifies that the ProCursor module wiring resolves its gateway/coordinator services and hosted worker.
/// </summary>
public sealed class ProCursorIndexWorkerRegistrationTests(
    ProCursorIndexWorkerRegistrationTests.ProCursorFactory factory)
    : IClassFixture<ProCursorIndexWorkerRegistrationTests.ProCursorFactory>
{
    [Fact]
    public void ApplicationServices_RegisterProCursorGatewayAndCoordinator()
    {
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProCursorOptions>>().Value;
        var gateway = scope.ServiceProvider.GetRequiredService<IProCursorGateway>();
        var coordinator = scope.ServiceProvider.GetRequiredService<ProCursorIndexCoordinator>();

        Assert.Equal(17, options.RefreshPollSeconds);
        Assert.Equal(4, options.MaxIndexConcurrency);
        Assert.NotNull(gateway);
        Assert.NotNull(coordinator);
    }

    [Fact]
    public void HostedServices_RegisterProCursorIndexWorkerAsSingletonHostedService()
    {
        var worker = factory.Services.GetRequiredService<ProCursorIndexWorker>();
        var hostedServices = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hostedServices, hostedService => ReferenceEquals(hostedService, worker));
    }

    public sealed class ProCursorFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-jwt-secret-32chars!!");
            builder.UseSetting("PROCURSOR_REFRESH_POLL_SECONDS", "17");
            builder.UseSetting("PROCURSOR_MAX_INDEX_CONCURRENCY", "4");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientAdoCredentialRepository>());
                services.AddSingleton(Substitute.For<IClientAdoOrganizationScopeRepository>());
                services.AddSingleton(Substitute.For<IAdoDiscoveryService>());
                services.AddSingleton(Substitute.For<IProtocolRecorder>());
                services.AddSingleton(Substitute.For<IMemoryActivityLog>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());
                services.AddSingleton(Substitute.For<IReviewPrScanRepository>());
                services.AddSingleton(Substitute.For<IMentionScanRepository>());
                services.AddSingleton(Substitute.For<IMentionReplyJobRepository>());

                var crawlConfigurationRepository = Substitute.For<ICrawlConfigurationRepository>();
                crawlConfigurationRepository.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Application.DTOs.CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlConfigurationRepository);

                var clientAdminService = Substitute.For<IClientAdminService>();
                clientAdminService.ExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(false));
                services.AddSingleton(clientAdminService);

                var knowledgeSourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
                knowledgeSourceRepository.ListByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([]));
                services.AddSingleton(knowledgeSourceRepository);
                services.AddSingleton(Substitute.For<IProCursorIndexJobRepository>());
                services.AddSingleton(Substitute.For<IProCursorIndexSnapshotRepository>());
                services.AddSingleton(Substitute.For<IProCursorSymbolGraphRepository>());

                var jobRepository = Substitute.For<IJobRepository>();
                jobRepository.GetProcessingJobsAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
                services.AddSingleton(jobRepository);

                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepository = Substitute.For<IUserRepository>();
                userRepository.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepository.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepository);

                var aiConnectionRepository = Substitute.For<IAiConnectionRepository>();
                aiConnectionRepository.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Application.DTOs.AiConnectionDto>>([]));
                services.AddSingleton(aiConnectionRepository);
            });
        }
    }
}
