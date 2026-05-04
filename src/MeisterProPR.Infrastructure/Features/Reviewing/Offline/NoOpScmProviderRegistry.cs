// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline placeholder for registry consumers that should never execute in the evaluation harness.
/// </summary>
public sealed class NoOpScmProviderRegistry : IScmProviderRegistry
{
    public bool IsRegistered(ScmProvider provider)
    {
        return false;
    }

    public IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider)
    {
        return [];
    }

    public IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider) => throw CreateUnavailableException(provider);

    public ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider) => throw CreateUnavailableException(provider);

    public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider) => throw CreateUnavailableException(provider);

    public IWebhookIngressService GetWebhookIngressService(ScmProvider provider) => throw CreateUnavailableException(provider);

    private static InvalidOperationException CreateUnavailableException(ScmProvider provider)
    {
        return new InvalidOperationException(
            $"Provider registry access for '{provider}' is unavailable in offline review-evaluation mode.");
    }
}
