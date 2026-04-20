// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Services;
using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients.Support;
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

namespace MeisterProPR.Infrastructure.Features.Clients;

/// <summary>
///     Extension methods for registering the Clients module.
/// </summary>
public static class ClientsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers client administration and AI connection services.
    /// </summary>
    public static IServiceCollection AddClientsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
            services.TryAddSingleton<IProviderReadinessProfileCatalog, StaticProviderReadinessProfileCatalog>();
            services.TryAddScoped<IProviderActivationService, ProviderActivationService>();
            services.AddAzureDevOpsProviderAdapters();
            services.AddGitHubProviderAdapters();
            services.AddGitLabProviderAdapters();
            services.AddForgejoProviderAdapters();
            services.AddScoped<IClientRegistry, DbClientRegistry>();
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<IClientScmConnectionRepository, ClientScmConnectionRepository>();
            services.AddScoped<IClientScmScopeRepository, ClientScmScopeRepository>();
            services.AddScoped<IClientReviewerIdentityRepository, ClientReviewerIdentityRepository>();
            services.AddScoped<IProviderReadinessEvaluator, ProviderReadinessEvaluator>();
            services.AddScoped<IProviderOperationalStatusService, ProviderOperationalStatusService>();
            services.AddScoped<IAiConnectionRepository, AiConnectionRepository>();
        }

        return services;
    }
}
