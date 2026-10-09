// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Parsing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Runtime;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;

internal static class GitLabProviderServiceCollectionExtensions
{
    internal static IServiceCollection AddGitLabLocalPolicies(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReviewSourcePolicy, GitLabReviewSourcePolicy>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWebhookIngressPolicy,
                MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Webhooks.GitLabWebhookIngressPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScmConnectionConfigurationPolicy, GitLabConnectionConfigurationPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScmIdentityPolicy, GitLabIdentityPolicy>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICodeReviewPreparationPolicy,
                MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing.GitLabReviewPreparationPolicy>());
        return services;
    }

    public static IServiceCollection AddGitLabProviderAdapters(this IServiceCollection services)
    {
        services.AddPostedCommentComposer();
        services.AddGitLabLocalPolicies();

        services.AddHttpClient("GitLabProvider").GuardEgress();

        services.TryAddScoped<GitLabConnectionVerifier>();
        services.TryAddScoped<GitLabWebhookTokenVerifier>();
        services.TryAddScoped<GitLabWebhookEventClassifier>();
        services.TryAddScoped<GitLabWebhookPayloadParser>();
        services.TryAddScoped<GitLabReviewThreadStatusProvider>();
        services.TryAddScoped<GitLabReviewThreadStatusWriter>();
        services.TryAddScoped<GitLabReviewThreadReplyPublisher>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadStatusWriter, GitLabReviewThreadStatusWriter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewThreadReplyPublisher, GitLabReviewThreadReplyPublisher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewWorkspaceRemoteResolver, GitLabReviewWorkspaceRemoteResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, GitLabReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, GitLabPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IActivePullRequestDiscoveryProvider, GitLabActivePrFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILinkedItemProvider, GitLabLinkedItemProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, GitLabReviewContextToolsFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRepositoryDiscoveryProvider, GitLabDiscoveryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewerIdentityService, GitLabReviewerIdentityService>());
        services.TryAddKeyedScoped<IReviewerIdentityService, GitLabReviewerIdentityService>(MeisterDev.ProPR.Domain.Enums.ScmProvider.GitLab);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewQueryService, GitLabCodeReviewQueryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewPublicationService, GitLabCodeReviewPublicationService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewDiscoveryProvider, GitLabReviewDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewOverviewProvider, GitLabReviewOverviewProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookIngressService, GitLabWebhookIngressService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryInstructionFetcher, GitLabRepositoryInstructionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, GitLabRepositoryExclusionFetcher>());

        return services;
    }
}
