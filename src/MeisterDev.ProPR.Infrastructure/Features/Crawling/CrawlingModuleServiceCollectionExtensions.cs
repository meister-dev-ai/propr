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

            // Where a verified delivery waits between being accepted and becoming a review. Registered
            // beside the delivery log because they are the two halves of the same story: one records what
            // was decided, the other holds the work that decision created.
            services.AddScoped<IWebhookDeliveryQueue, WebhookDeliveryQueue>();
            services.AddScoped<IReviewPrScanRepository, EfReviewPrScanRepository>();

            // Callers that own only the last-seen thread status resolve the narrow port, which cannot
            // express a watermark or reply-count write.
            services.AddScoped<IReviewPrScanThreadStatusStore>(sp => sp.GetRequiredService<IReviewPrScanRepository>());

            // The file pass reaches the review watermark and nothing else; the thread pass reaches the thread
            // watermark and the per-thread counters and nothing else. Neither port can express the other's write.
            services.AddScoped<IReviewPrScanWatermarkStore>(sp => sp.GetRequiredService<IReviewPrScanRepository>());
            services.AddScoped<IReviewPrScanThreadPassStore>(sp => sp.GetRequiredService<IReviewPrScanRepository>());

            // The guard that declines an increment records which revision it declined, and reaches nothing else.
            services.AddScoped<IReviewPrScanPendingReviewWriter>(sp => sp.GetRequiredService<IReviewPrScanRepository>());

            // Read surfaces report the scan record without being able to write any part of it.
            services.AddScoped<IReviewPrScanReader>(sp => sp.GetRequiredService<IReviewPrScanRepository>());
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
