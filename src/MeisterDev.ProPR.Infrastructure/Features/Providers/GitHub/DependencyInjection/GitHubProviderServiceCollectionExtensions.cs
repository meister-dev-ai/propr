// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Parsing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Runtime;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;

internal static class GitHubProviderServiceCollectionExtensions
{
    internal static IServiceCollection AddGitHubLocalPolicies(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReviewSourcePolicy, GitHubReviewSourcePolicy>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWebhookIngressPolicy,
                MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Webhooks.GitHubWebhookIngressPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScmConnectionConfigurationPolicy, GitHubConnectionConfigurationPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScmIdentityPolicy, GitHubIdentityPolicy>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICodeReviewPreparationPolicy,
                MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing.GitHubReviewPreparationPolicy>());
        return services;
    }

    public static IServiceCollection AddGitHubProviderAdapters(this IServiceCollection services)
    {
        services.AddPostedCommentComposer();
        services.AddGitHubLocalPolicies();

        services.AddHttpClient(
                "GitHubProvider",
                client =>
                {
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("MeisterDev.ProPR");
                })
            .GuardEgress();

        services.TryAddSingleton<GitHubInstallationTokenCache>();
        services.TryAddScoped<GitHubAuthenticationService>();
        services.TryAddScoped<GitHubConnectionVerifier>();
        services.TryAddScoped<GitHubWebhookSignatureVerifier>();
        services.TryAddScoped<GitHubWebhookEventClassifier>();
        services.TryAddScoped<GitHubWebhookPayloadParser>();
        services.TryAddScoped<GitHubReviewAssignmentProvider>();
        services.TryAddScoped<GitHubLifecyclePublicationService>();
        services.TryAddScoped<GitHubRepositoryExclusionFetcher>();
        services.TryAddScoped<GitHubReviewThreadStatusProvider>();
        services.TryAddScoped<GitHubReviewThreadStatusWriter>();
        services.TryAddScoped<GitHubReviewThreadReplyPublisher>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadStatusWriter, GitHubReviewThreadStatusWriter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadReplyPublisher, GitHubReviewThreadReplyPublisher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewWorkspaceRemoteResolver, GitHubReviewWorkspaceRemoteResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, GitHubRepositoryExclusionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, GitHubReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, GitHubPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IActivePullRequestDiscoveryProvider, GitHubActivePrFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILinkedItemProvider, GitHubLinkedItemProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, GitHubReviewContextToolsFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRepositoryDiscoveryProvider, GitHubDiscoveryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewerIdentityService, GitHubReviewerIdentityService>());
        services.TryAddKeyedScoped<IReviewerIdentityService, GitHubReviewerIdentityService>(MeisterDev.ProPR.Domain.Enums.ScmProvider.GitHub);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewQueryService, GitHubCodeReviewQueryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewPublicationService, GitHubCodeReviewPublicationService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewDiscoveryProvider, GitHubReviewDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewOverviewProvider, GitHubReviewOverviewProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewAssignmentService, GitHubReviewAssignmentProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookIngressService, GitHubWebhookIngressService>());

        return services;
    }
}
