// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Discovery;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Identity;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;

internal static class ForgejoProviderServiceCollectionExtensions
{
    public static IServiceCollection AddForgejoProviderAdapters(this IServiceCollection services)
    {
        services.AddHttpClient("ForgejoProvider");

        services.TryAddScoped<ForgejoConnectionVerifier>();
        services.TryAddScoped<ForgejoWebhookSignatureVerifier>();
        services.TryAddScoped<ForgejoWebhookEventClassifier>();
        services.TryAddScoped<ForgejoWebhookPayloadParser>();
        services.TryAddScoped<ForgejoReviewThreadStatusProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, ForgejoReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, ForgejoRepositoryExclusionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, ForgejoPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, ForgejoReviewContextToolsFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRepositoryDiscoveryProvider, ForgejoDiscoveryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewerIdentityService, ForgejoReviewerIdentityService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewQueryService, ForgejoCodeReviewQueryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewPublicationService, ForgejoCodeReviewPublicationService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewDiscoveryProvider, ForgejoReviewDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookIngressService, ForgejoWebhookIngressService>());

        return services;
    }
}
