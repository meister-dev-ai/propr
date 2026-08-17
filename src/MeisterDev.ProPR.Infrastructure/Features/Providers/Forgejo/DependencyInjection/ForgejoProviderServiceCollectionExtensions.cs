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
        // No IReviewThreadStatusWriter, so Forgejo advertises no reviewThreadStatus and callers degrade on
        // the advertised set. Forgejo's REST API exposes no thread: a review comment carries a path, a
        // position and the review it was submitted under, and nothing that names the conversation it sits in,
        // so there is no thread to mark resolved.
        //
        // Replying is a different question, and Forgejo answers it by convention rather than by structure:
        // its own quote reply posts a new comment opening with a markdown blockquote of the one it answers,
        // and quotes nest, so a conversation stays followable. ForgejoReviewThreadReplyPublisher does exactly
        // that, which is why reviewThreadReply is advertised where reviewThreadStatus is not.
        services.TryAddScoped<ForgejoReviewThreadStatusProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadReplyPublisher, ForgejoReviewThreadReplyPublisher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewWorkspaceRemoteResolver, ForgejoReviewWorkspaceRemoteResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, ForgejoReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, ForgejoRepositoryExclusionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, ForgejoPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IActivePullRequestDiscoveryProvider, ForgejoActivePrFetcher>());
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
