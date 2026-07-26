// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.ProPR.Application.Features.Clients.Services;
using MeisterDev.ProPR.Application.Features.Clients.Support;
using MeisterDev.ProPR.Application.Interfaces;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                var gitHubAuthenticationService = sp.GetRequiredService<GitHubAuthenticationService>();
                var logger = sp.GetRequiredService<ILogger<DbClientRegistry>>();

                return new DbClientRegistry(
                    dbContext,
                    connectionRepository,
                    reviewerIdentityRepository,
                    async (host, connection, ct) =>
                    {
                        if (host.Provider != ScmProvider.GitHub || connection.AuthenticationKind != ScmAuthenticationKind.AppInstallation)
                        {
                            return null;
                        }

                        var app = await gitHubAuthenticationService.GetAppMetadataAsync(host, connection, ct);
                        var login = app.Slug + "[bot]";
                        return new ReviewerIdentity(host, login, login, app.DisplayName, true);
                    },
                    logger);
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
        }

        return services;
    }
}
