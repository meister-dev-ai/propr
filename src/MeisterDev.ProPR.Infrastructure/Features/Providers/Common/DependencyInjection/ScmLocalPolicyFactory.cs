// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

/// <summary>Composes pure native policies for offline hosts without creating live adapters or a service container.</summary>
public static class ScmLocalPolicyFactory
{
    public static IReviewSourcePolicy[] CreateSourcePolicies() =>
        [new AdoReviewSourcePolicy(), new GitHubReviewSourcePolicy(), new GitLabReviewSourcePolicy(), new ForgejoReviewSourcePolicy()];

    public static IScmConnectionConfigurationPolicy[] CreateConfigurationPolicies() =>
    [
        new AdoConnectionConfigurationPolicy(), new GitHubConnectionConfigurationPolicy(), new GitLabConnectionConfigurationPolicy(),
        new ForgejoConnectionConfigurationPolicy()
    ];

    public static IScmIdentityPolicy[] CreateIdentityPolicies() =>
        [new AdoIdentityPolicy(), new GitHubIdentityPolicy(), new GitLabIdentityPolicy(), new ForgejoIdentityPolicy()];

    public static IWebhookIngressPolicy[] CreateWebhookIngressPolicies() =>
    [
        new MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Webhooks.AdoWebhookIngressPolicy(),
        new MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Webhooks.GitHubWebhookIngressPolicy(),
        new MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Webhooks.GitLabWebhookIngressPolicy(),
        new MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Webhooks.ForgejoWebhookIngressPolicy()
    ];

    public static IScmProviderRegistry CreateRegistry() => new ScmProviderRegistry(
        [], [], [], [], [], [], [], [], [], [], [], [],
        reviewSourcePolicies: CreateSourcePolicies(),
        connectionConfigurationPolicies: CreateConfigurationPolicies(),
        identityPolicies: CreateIdentityPolicies(),
        preparationPolicies:
        [new AdoReviewPreparationPolicy(), new GitHubReviewPreparationPolicy(), new GitLabReviewPreparationPolicy(), new ForgejoReviewPreparationPolicy()],
        webhookIngressPolicies: CreateWebhookIngressPolicies(),
        compatibilityCodec: new MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility.ScmProviderCompatibilityCodec());
}
