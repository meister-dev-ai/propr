// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Services;
using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients.Support;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            services.AddScoped<ILogicalModelCapabilityValidator, LogicalModelCapabilityValidator>();
            services.AddScoped<ILogicalModelCatalogRepository, LogicalModelCatalogRepository>();
            services.AddScoped<ILogicalModelResolver, LogicalModelResolver>();
            services.AddScoped<ILogicalModelMigrationBackfill, LogicalModelMigrationBackfill>();
        }

        return services;
    }
}
