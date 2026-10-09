// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.DependencyInjection;

public sealed class AdoWebhookCompatibilityCompositionTests
{
    [Fact]
    public void NativeCompatibilityServicesRetainTheExistingLoggingCategories()
    {
        var services = new ServiceCollection();
        var loggers = Substitute.For<ILoggerFactory>();
        loggers.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        services.AddSingleton(loggers);
        services.AddSingleton(Substitute.For<IPullRequestIterationResolver>());
        services.AddSingleton(Substitute.For<IJobRepository>());
        services.AddSingleton(
            new SubmitReviewJobHandler(
                Substitute.For<IReviewJobIntakeStore>(), Substitute.For<IReviewExecutionQueue>(),
                NullLogger<SubmitReviewJobHandler>.Instance));
        services.AddAzureDevOpsCrawlingServices(new ConfigurationBuilder().Build());
        using var host = services.BuildServiceProvider();
        using var scope = host.CreateScope();

        Assert.IsType<WebhookReviewActivationService>(scope.ServiceProvider.GetRequiredService<IWebhookReviewActivationService>());
        Assert.IsType<WebhookReviewLifecycleSyncService>(scope.ServiceProvider.GetRequiredService<IWebhookReviewLifecycleSyncService>());
        loggers.Received(1).CreateLogger("MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Services.WebhookReviewActivationService");
        loggers.Received(1).CreateLogger("MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Services.WebhookReviewLifecycleSyncService");
    }
}
