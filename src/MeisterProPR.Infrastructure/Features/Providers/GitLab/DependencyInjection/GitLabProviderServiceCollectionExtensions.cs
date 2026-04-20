// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Discovery;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Identity;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;

internal static class GitLabProviderServiceCollectionExtensions
{
    public static IServiceCollection AddGitLabProviderAdapters(this IServiceCollection services)
    {
        services.AddHttpClient("GitLabProvider");

        services.TryAddScoped<GitLabConnectionVerifier>();
        services.TryAddScoped<GitLabWebhookTokenVerifier>();
        services.TryAddScoped<GitLabWebhookEventClassifier>();
        services.TryAddScoped<GitLabWebhookPayloadParser>();
        services.TryAddScoped<GitLabReviewThreadStatusProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewerThreadStatusFetcher, GitLabReviewThreadStatusProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderPullRequestFetcher, GitLabPullRequestFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderReviewContextToolsFactory, GitLabReviewContextToolsFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRepositoryDiscoveryProvider, GitLabDiscoveryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewerIdentityService, GitLabReviewerIdentityService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewQueryService, GitLabCodeReviewQueryService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICodeReviewPublicationService, GitLabCodeReviewPublicationService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IReviewDiscoveryProvider, GitLabReviewDiscoveryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookIngressService, GitLabWebhookIngressService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryInstructionFetcher, GitLabRepositoryInstructionFetcher>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProviderRepositoryExclusionFetcher, GitLabRepositoryExclusionFetcher>());

        return services;
    }
}
