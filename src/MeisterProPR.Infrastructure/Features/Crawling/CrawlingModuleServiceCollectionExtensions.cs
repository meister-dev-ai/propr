// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Execution.Services;
using MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Security;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.Crawling;

/// <summary>
///     Extension methods for registering the Crawling module.
/// </summary>
public static class CrawlingModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers crawling configuration and execution services.
    /// </summary>
    public static IServiceCollection AddCrawlingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        services.AddAzureDevOpsProviderAdapters();
        services.AddGitHubProviderAdapters();
        services.AddGitLabProviderAdapters();
        services.AddForgejoProviderAdapters();

        if (configuration.HasDatabaseConnectionString())
        {
            services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
            services.AddScoped<ICrawlConfigurationRepository, CrawlConfigurationRepository>();
            services.AddScoped<IWebhookConfigurationRepository, EfWebhookConfigurationRepository>();
            services.AddScoped<IWebhookDeliveryLogRepository, EfWebhookDeliveryLogRepository>();
            services.AddScoped<IReviewPrScanRepository, EfReviewPrScanRepository>();
        }

        services.AddAzureDevOpsCrawlingServices(configuration);
        services.AddScoped<IPullRequestSynchronizationService, PullRequestSynchronizationService>();
        services.AddScoped<IWebhookReviewActivationService, WebhookReviewActivationService>();
        services.AddScoped<IWebhookReviewLifecycleSyncService, WebhookReviewLifecycleSyncService>();
        services.AddScoped<HandleProviderWebhookDeliveryHandler>();

        services.AddScoped<IWebhookSecretGenerator, WebhookSecretGenerator>();
        services.AddScoped<IPrCrawlService, PrCrawlService>();

        return services;
    }
}
