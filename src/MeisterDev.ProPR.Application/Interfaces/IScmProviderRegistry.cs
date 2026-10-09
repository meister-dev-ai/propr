// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Resolves provider-family capabilities from the registered adapter set.</summary>
public interface IScmProviderRegistry
{
    /// <summary>Decodes stored boundary evidence without enabling operational capabilities.</summary>
    IScmProviderCompatibilityCodec CompatibilityCodec { get; }

    /// <summary>Resolves credential-free native webhook diagnostic and retry metadata.</summary>
    IWebhookIngressPolicy GetWebhookIngressPolicy(ScmProvider provider);

    /// <summary>Resolves credential-free native identity rules.</summary>
    IScmIdentityPolicy GetIdentityPolicy(ScmProvider provider);

    /// <summary>Resolves credential-free revision, comparison and publication preparation.</summary>
    ICodeReviewPreparationPolicy GetCodeReviewPreparationPolicy(ScmProvider provider);

    /// <summary>Resolves local captured-source projection, including historical unknown provider values.</summary>
    IReviewSourcePolicy GetSourceIdentityPolicy(ScmProvider provider);

    /// <summary>Resolves local native authentication configuration rules.</summary>
    IScmConnectionConfigurationPolicy GetConnectionConfigurationPolicy(ScmProvider provider);

    /// <summary>
    ///     Returns <c>true</c> when the provider family has the baseline adapter set used for onboarding,
    ///     core review query/publication, and webhook ingress.
    /// </summary>
    bool IsRegistered(ScmProvider provider);

    /// <summary>Returns the capability names that are currently registered for the given provider family.</summary>
    IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider);

    /// <summary>Resolves repository and scope discovery for the given provider family.</summary>
    IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider);

    /// <summary>
    ///     Reports whether active pull-request discovery is registered for the given provider family, which
    ///     decides whether the provider can answer mentions at all.
    /// </summary>
    bool SupportsActivePullRequestDiscovery(ScmProvider provider);

    /// <summary>
    ///     Reports whether a reply can be published into a review thread on the given provider family, which is
    ///     the other half of what mention answering needs: finding the question, and answering where it was
    ///     asked.
    /// </summary>
    bool SupportsReviewThreadReply(ScmProvider provider);

    /// <summary>
    ///     Reports whether replying on the given provider family needs the thread's own identifier, which
    ///     decides whether a comment belonging to no addressable thread can still be answered.
    /// </summary>
    /// <remarks>
    ///     True when no reply publisher is registered, so a caller reading this before deciding what to accept
    ///     never widens what it accepts because a provider is absent.
    /// </remarks>
    bool RequiresReviewThreadIdentifier(ScmProvider provider);

    /// <summary>Resolves review-query capabilities for the given provider family.</summary>
    ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider);

    /// <summary>Resolves review-publication capabilities for the given provider family.</summary>
    ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider);

    /// <summary>Resolves review-discovery capabilities for the given provider family.</summary>
    IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider);

    /// <summary>Resolves bounded pull request overview metadata for the given provider family.</summary>
    IReviewOverviewProvider GetReviewOverviewProvider(ScmProvider provider);

    /// <summary>Resolves local review-source scope and identity rules for the provider family.</summary>
    IReviewSourcePolicy GetReviewSourcePolicy(ScmProvider provider);

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

    /// <summary>Resolves linked-work-item / linked-issue retrieval for the given provider family.</summary>
    ILinkedItemProvider GetLinkedItemProvider(ScmProvider provider);
}
