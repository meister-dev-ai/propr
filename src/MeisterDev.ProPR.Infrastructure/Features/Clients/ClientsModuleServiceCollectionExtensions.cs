// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.ProPR.Application.Features.Clients.Services;
using MeisterDev.ProPR.Application.Features.Clients.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Clients.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Clients;

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
        services.AddScmProviderLocalDeclarations();
        services.TryAddScoped<IProviderConnectionConfigurationService, ProviderConnectionConfigurationService>();
        // The provider families enabled until an administrator changes their activation. A name outside the supported
        // families stops the start, so a typing error does not leave a provider family disabled unnoticed.
        services.AddOptions<ProviderActivationOptions>()
            .Configure(options =>
            {
                var setting = configuration[ProviderActivationOptions.DefaultEnabledKey];
                if (string.IsNullOrWhiteSpace(setting))
                {
                    return;
                }

                var enabled = new HashSet<ScmProvider>();
                foreach (var name in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!char.IsLetter(name[0]) || !Enum.TryParse<ScmProvider>(name, true, out var provider) ||
                        !Enum.IsDefined(provider))
                    {
                        throw new OptionsValidationException(
                            Microsoft.Extensions.Options.Options.DefaultName,
                            typeof(ProviderActivationOptions),
                            [
                                $"{ProviderActivationOptions.DefaultEnabledKey} names the unknown provider family '{name}'. Use azureDevOps, github, gitLab or forgejo, separated by commas."
                            ]);
                    }

                    enabled.Add(provider);
                }

                // A value of commas only names no provider family and would disable every family without notice.
                if (enabled.Count == 0)
                {
                    throw new OptionsValidationException(
                        Microsoft.Extensions.Options.Options.DefaultName,
                        typeof(ProviderActivationOptions),
                        [
                            $"{ProviderActivationOptions.DefaultEnabledKey} names no provider family. Use azureDevOps, github, gitLab or forgejo, separated by commas, or leave it unset."
                        ]);
                }

                options.DefaultEnabled = enabled;
            })
            .ValidateOnStart();

        if (configuration.HasDatabaseConnectionString())
        {
            services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
            services.TryAddSingleton<IProviderReadinessProfileCatalog, StaticProviderReadinessProfileCatalog>();
            services.TryAddScoped<IProviderActivationService, ProviderActivationService>();
            services.AddAzureDevOpsProviderAdapters();
            services.AddGitHubProviderAdapters();
            services.AddGitLabProviderAdapters();
            services.AddForgejoProviderAdapters();
            services.AddScoped<IClientRegistry>(sp =>
            {
                var dbContext = sp.GetRequiredService<MeisterProPRDbContext>();
                var connectionRepository = sp.GetRequiredService<IClientScmConnectionRepository>();
                var reviewerIdentityRepository = sp.GetRequiredService<IClientReviewerIdentityRepository>();
                var logger = sp.GetRequiredService<ILogger<DbClientRegistry>>();

                return new DbClientRegistry(
                    dbContext,
                    connectionRepository,
                    reviewerIdentityRepository,
                    async (host, connection, ct) =>
                    {
                        var service = sp.GetKeyedService<IReviewerIdentityService>(host.Provider);
                        return service is null ? null : await service.GetAutomaticReviewerIdentityAsync(host, connection, ct);
                    },
                    logger,
                    sp.GetService<IDbContextFactory<MeisterProPRDbContext>>(),
                    sp.GetServices<IScmIdentityPolicy>());
            });
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<IClientScmConnectionRepository, ClientScmConnectionRepository>();
            services.AddScoped<IClientScmScopeRepository, ClientScmScopeRepository>();
            services.AddScoped<IClientReviewerIdentityRepository, ClientReviewerIdentityRepository>();
            services.AddScoped<IProviderReadinessEvaluator, ProviderReadinessEvaluator>();
            services.AddScoped<IProviderOperationalStatusService, ProviderOperationalStatusService>();
            services.AddScoped<IAiConnectionRepository, AiConnectionRepository>();
            services.AddScoped<IAiConnectionScopeGuard, AiConnectionScopeGuard>();
            services.AddScoped<ITenantProviderPolicyProvider, TenantProviderPolicyProvider>();
            services.AddScoped<ITenantReasoningCapturePolicyProvider, TenantReasoningCapturePolicyProvider>();
            services.AddScoped<IAiProviderConfigAuditWriter, TenantAuditAiProviderConfigWriter>();
            services.AddSingleton<ICatalogSnapshotImporter, ModelsDevCatalogSnapshotImporter>();
            // Registered here as well as by the Reviewing module: catalog import needs a clock and must not
            // depend on another module happening to be composed first.
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<IModelCatalogImportService, ModelCatalogImportService>();
            services.AddScoped<IModelCatalogRepository, ModelCatalogRepository>();
            services.AddScoped<ILogicalModelCapabilityValidator, LogicalModelCapabilityValidator>();
            services.AddScoped<ILogicalModelCatalogRepository, LogicalModelCatalogRepository>();
            services.AddScoped<ILogicalModelResolver, LogicalModelResolver>();
            services.AddScoped<ILogicalModelMigrationBackfill, LogicalModelMigrationBackfill>();
            services.AddScoped<IConfiguredModelReasoningLevelBackfill, ConfiguredModelReasoningLevelBackfill>();
        }

        return services;
    }
}
