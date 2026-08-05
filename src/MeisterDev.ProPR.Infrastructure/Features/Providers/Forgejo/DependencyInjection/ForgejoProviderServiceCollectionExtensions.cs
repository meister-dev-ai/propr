// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Parsing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Runtime;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;

internal static class ForgejoProviderServiceCollectionExtensions
{
    public static IServiceCollection AddForgejoProviderAdapters(this IServiceCollection services)
    {
        services.AddHttpClient("ForgejoProvider");

        services.TryAddScoped<ForgejoConnectionVerifier>();
        services.TryAddScoped<ForgejoWebhookSignatureVerifier>();
        services.TryAddScoped<ForgejoWebhookEventClassifier>();
        services.TryAddScoped<ForgejoWebhookPayloadParser>();
        // No IReviewThreadStatusWriter and no IReviewThreadReplyPublisher, so Forgejo advertises neither
        // reviewThreadStatus nor reviewThreadReply and callers degrade on the advertised set.
        // Forgejo's REST API exposes no thread: a review comment carries a path, a position and the review it
        // was submitted under, and nothing that names the conversation it sits in. The routes it does offer
        // create a comment on a review, never a reply to one, and the request to add threaded replies was
        // closed upstream without one. Gitea added a reply route after the fork and it has not been ported.
        // Posting a fresh comment at the same path and line would render near the conversation without being
        // part of it, and after threads began carrying their provider's own identifier Forgejo carries none
        // to anchor such a comment to, so the capability stays unadvertised rather than approximated.
        services.TryAddScoped<ForgejoReviewThreadStatusProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewWorkspaceRemoteResolver, ForgejoReviewWorkspaceRemoteResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, ForgejoReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, ForgejoRepositoryExclusionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, ForgejoPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILinkedItemProvider, ForgejoLinkedItemProvider>());
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
