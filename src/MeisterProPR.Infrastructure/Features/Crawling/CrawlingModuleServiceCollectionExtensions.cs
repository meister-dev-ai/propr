// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddCrawlingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<ICrawlConfigurationRepository, CrawlConfigurationRepository>();
            services.AddScoped<IClientAdoCredentialRepository, ClientAdoCredentialRepository>();
            services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();
            services.AddScoped<IAdoDiscoveryService, AdoDiscoveryService>();
            services.AddScoped<IReviewPrScanRepository, EfReviewPrScanRepository>();
        }

        services.AddScoped<IPrCrawlService, PrCrawlService>();

        return services;
    }
}
