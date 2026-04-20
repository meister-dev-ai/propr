// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;

internal static class AzureDevOpsProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAzureDevOpsProviderAdapters(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRepositoryDiscoveryProvider, AdoRepositoryDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewerIdentityService, AdoReviewerIdentityService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewQueryService, AdoCodeReviewQueryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewPublicationService, AdoCodeReviewPublicationService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewDiscoveryProvider, AdoReviewDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookIngressService, AdoWebhookIngressService>());

        return services;
    }

    public static IServiceCollection AddAzureDevOpsInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        TokenCredential? credential = null)
    {
        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, StubPullRequestFetcher>());
            services.TryAddScoped<IPullRequestFetcher, ProviderPullRequestFetcher>();
            services.TryAddScoped<IAdoCommentPoster, NoOpAdoCommentPoster>();
            services.TryAddScoped<IAssignedReviewDiscoveryService, StubAssignedPrFetcher>();
            services.TryAddScoped<IPrStatusFetcher, StubPrStatusFetcher>();
            services.TryAddScoped<IIdentityResolver, StubIdentityResolver>();
            services.TryAddScoped<StubAdoReviewerManager>();
            services.TryAddScoped<IActivePrFetcher, StubActivePrFetcher>();
            services.TryAddScoped<StubAdoThreadReplier>();
            services.TryAddScoped<StubAdoThreadClient>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewAssignmentService, StubAdoReviewerManager>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadReplyPublisher, StubAdoThreadReplier>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadStatusWriter, StubAdoThreadClient>());

            return services;
        }

        ArgumentNullException.ThrowIfNull(credential);

        services.TryAddSingleton(_ => new VssConnectionFactory(credential));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, AdoPrFetcher>());
        services.TryAddScoped<IPullRequestFetcher, ProviderPullRequestFetcher>();
        services.TryAddScoped<IAdoCommentPoster, AdoCommentPoster>();
        services.TryAddScoped<IAssignedReviewDiscoveryService, AdoAssignedPrFetcher>();
        services.TryAddScoped<IPrStatusFetcher, AdoPrStatusFetcher>();
        services.AddHttpClient("AdoIdentity");
        services.TryAddScoped<IIdentityResolver>(sp =>
            new AdoIdentityResolver(
                credential,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IClientScmConnectionRepository>()));
        services.TryAddScoped<AdoReviewerManager>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewAssignmentService, AdoReviewerManager>());
        services.TryAddScoped<IActivePrFetcher, AdoActivePrFetcher>();
        services.TryAddScoped<AdoThreadReplier>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadReplyPublisher, AdoThreadReplier>());
        services.TryAddScoped<AdoThreadClient>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadStatusWriter, AdoThreadClient>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, AdoReviewerThreadStatusFetcher>());

        return services;
    }

    public static IServiceCollection AddAzureDevOpsCrawlingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.TryAddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderAdminDiscoveryService, AdoDiscoveryService>());
        }

        services.TryAddScoped<IAdoWebhookBasicAuthVerifier, AdoWebhookBasicAuthVerifier>();
        services.TryAddScoped<IAdoWebhookPayloadParser, AdoWebhookPayloadParser>();

        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.TryAddScoped<IPullRequestIterationResolver, StubPullRequestIterationResolver>();
        }
        else
        {
            services.TryAddScoped<IPullRequestIterationResolver, AdoPullRequestIterationResolver>();
        }

        return services;
    }

    public static IServiceCollection AddAzureDevOpsReviewingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, StubReviewContextToolsFactory>());
            services.TryAddScoped<IReviewContextToolsFactory, ProviderReviewContextToolsFactory>();

            return services;
        }

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, AdoReviewContextToolsFactory>());
        services.TryAddScoped<IReviewContextToolsFactory, ProviderReviewContextToolsFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryInstructionFetcher, AdoRepositoryInstructionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, AdoRepositoryExclusionFetcher>());

        return services;
    }
}
