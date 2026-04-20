// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Resolves provider-family capabilities from the registered adapter set.</summary>
public interface IScmProviderRegistry
{
    /// <summary>
    ///     Returns <c>true</c> when the provider family has the baseline adapter set used for onboarding,
    ///     core review query/publication, and webhook ingress.
    /// </summary>
    bool IsRegistered(ScmProvider provider);

    /// <summary>Returns the capability names that are currently registered for the given provider family.</summary>
    IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider);

    /// <summary>Resolves repository and scope discovery for the given provider family.</summary>
    IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider);

    /// <summary>Resolves review-query capabilities for the given provider family.</summary>
    ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider);

    /// <summary>Resolves review-publication capabilities for the given provider family.</summary>
    ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider);

    /// <summary>Resolves review-discovery capabilities for the given provider family.</summary>
    IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider);

    /// <summary>Resolves reviewer-identity capabilities for the given provider family.</summary>
    IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider);

    /// <summary>Resolves reviewer-assignment capabilities for the given provider family.</summary>
    IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider);

    /// <summary>Resolves review-thread status mutation for the given provider family.</summary>
    IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider);

    /// <summary>Resolves review-thread reply publication for the given provider family.</summary>
    IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider);

    /// <summary>Resolves provider-backed admin discovery for the given provider family.</summary>
    IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider);

    /// <summary>Resolves webhook-ingress capabilities for the given provider family.</summary>
    IWebhookIngressService GetWebhookIngressService(ScmProvider provider);
}
