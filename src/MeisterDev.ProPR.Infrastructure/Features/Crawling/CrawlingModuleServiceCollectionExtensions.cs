// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Webhooks.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling;

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
