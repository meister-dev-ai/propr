// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline placeholder for registry consumers that should never execute in the evaluation harness.
/// </summary>
public sealed class NoOpScmProviderRegistry : IScmProviderRegistry
{
    private readonly IScmProviderRegistry _localPolicies =
        MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection.ScmLocalPolicyFactory.CreateRegistry();

    public IScmProviderCompatibilityCodec CompatibilityCodec => this._localPolicies.CompatibilityCodec;

    public IScmIdentityPolicy GetIdentityPolicy(ScmProvider provider) => this._localPolicies.GetIdentityPolicy(provider);
    public IWebhookIngressPolicy GetWebhookIngressPolicy(ScmProvider provider) => this._localPolicies.GetWebhookIngressPolicy(provider);
    public ICodeReviewPreparationPolicy GetCodeReviewPreparationPolicy(ScmProvider provider) => this._localPolicies.GetCodeReviewPreparationPolicy(provider);
    public IReviewSourcePolicy GetSourceIdentityPolicy(ScmProvider provider) => this._localPolicies.GetSourceIdentityPolicy(provider);

    public IScmConnectionConfigurationPolicy GetConnectionConfigurationPolicy(ScmProvider provider) =>
        this._localPolicies.GetConnectionConfigurationPolicy(provider);

    public bool IsRegistered(ScmProvider provider)
    {
        return false;
    }

    public IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider)
    {
        return [];
    }

    public IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public bool SupportsActivePullRequestDiscovery(ScmProvider provider)
    {
        return false;
    }

    public bool SupportsReviewThreadReply(ScmProvider provider)
    {
        return false;
    }

    public bool RequiresReviewThreadIdentifier(ScmProvider provider)
    {
        return true;
    }

    public ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewSourcePolicy GetReviewSourcePolicy(ScmProvider provider) => this._localPolicies.GetReviewSourcePolicy(provider);


    public IReviewOverviewProvider GetReviewOverviewProvider(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public IWebhookIngressService GetWebhookIngressService(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    public ILinkedItemProvider GetLinkedItemProvider(ScmProvider provider)
    {
        throw CreateUnavailableException(provider);
    }

    private static InvalidOperationException CreateUnavailableException(ScmProvider provider)
    {
        return new InvalidOperationException($"Provider registry access for '{provider}' is unavailable in offline review-evaluation mode.");
    }
}
