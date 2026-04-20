// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

/// <summary>Default registry for provider-family capability adapters.</summary>
internal sealed class ScmProviderRegistry(
    IEnumerable<IRepositoryDiscoveryProvider> repositoryDiscoveryProviders,
    IEnumerable<ICodeReviewQueryService> codeReviewQueryServices,
    IEnumerable<ICodeReviewPublicationService> codeReviewPublicationServices,
    IEnumerable<IReviewDiscoveryProvider> reviewDiscoveryProviders,
    IEnumerable<IReviewerIdentityService> reviewerIdentityServices,
    IEnumerable<IReviewAssignmentService> reviewAssignmentServices,
    IEnumerable<IReviewThreadStatusWriter> reviewThreadStatusWriters,
    IEnumerable<IReviewThreadReplyPublisher> reviewThreadReplyPublishers,
    IEnumerable<IProviderAdminDiscoveryService> providerAdminDiscoveryServices,
    IEnumerable<IWebhookIngressService> webhookIngressServices) : IScmProviderRegistry
{
    private readonly IReadOnlyDictionary<ScmProvider, ICodeReviewPublicationService>
        _codeReviewPublicationServicesByProvider =
            codeReviewPublicationServices.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, ICodeReviewQueryService> _codeReviewQueryServicesByProvider =
        codeReviewQueryServices.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IProviderAdminDiscoveryService>
        _providerAdminDiscoveryServicesByProvider =
            providerAdminDiscoveryServices.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IRepositoryDiscoveryProvider>
        _repositoryDiscoveryProvidersByProvider =
            repositoryDiscoveryProviders.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IReviewAssignmentService> _reviewAssignmentServicesByProvider =
        reviewAssignmentServices.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IReviewDiscoveryProvider> _reviewDiscoveryProvidersByProvider =
        reviewDiscoveryProviders.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IReviewerIdentityService> _reviewerIdentityServicesByProvider =
        reviewerIdentityServices.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IReviewThreadReplyPublisher>
        _reviewThreadReplyPublishersByProvider =
            reviewThreadReplyPublishers.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IReviewThreadStatusWriter> _reviewThreadStatusWritersByProvider =
        reviewThreadStatusWriters.ToDictionary(provider => provider.Provider);

    private readonly IReadOnlyDictionary<ScmProvider, IWebhookIngressService> _webhookIngressServicesByProvider =
        webhookIngressServices.ToDictionary(provider => provider.Provider);

    public bool IsRegistered(ScmProvider provider)
    {
        return this._repositoryDiscoveryProvidersByProvider.ContainsKey(provider) &&
               this._codeReviewQueryServicesByProvider.ContainsKey(provider) &&
               this._codeReviewPublicationServicesByProvider.ContainsKey(provider) &&
               this._reviewDiscoveryProvidersByProvider.ContainsKey(provider) &&
               this._reviewerIdentityServicesByProvider.ContainsKey(provider) &&
               this._webhookIngressServicesByProvider.ContainsKey(provider);
    }

    public IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider)
    {
        var capabilities = new List<string>(10);

        AddCapability(capabilities, this._repositoryDiscoveryProvidersByProvider, provider, "repositoryDiscovery");
        AddCapability(capabilities, this._codeReviewQueryServicesByProvider, provider, "codeReviewQuery");
        AddCapability(capabilities, this._codeReviewPublicationServicesByProvider, provider, "codeReviewPublication");
        AddCapability(capabilities, this._reviewDiscoveryProvidersByProvider, provider, "reviewDiscovery");
        AddCapability(capabilities, this._reviewerIdentityServicesByProvider, provider, "reviewerIdentity");
        AddCapability(capabilities, this._reviewAssignmentServicesByProvider, provider, "reviewAssignment");
        AddCapability(capabilities, this._reviewThreadStatusWritersByProvider, provider, "reviewThreadStatus");
        AddCapability(capabilities, this._reviewThreadReplyPublishersByProvider, provider, "reviewThreadReply");
        AddCapability(capabilities, this._providerAdminDiscoveryServicesByProvider, provider, "providerAdminDiscovery");
        AddCapability(capabilities, this._webhookIngressServicesByProvider, provider, "webhookIngress");

        return capabilities;
    }

    public IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider)
    {
        return GetRequired(
            this._repositoryDiscoveryProvidersByProvider,
            provider,
            nameof(IRepositoryDiscoveryProvider));
    }

    public ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider)
    {
        return GetRequired(this._codeReviewQueryServicesByProvider, provider, nameof(ICodeReviewQueryService));
    }

    public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider)
    {
        return GetRequired(
            this._codeReviewPublicationServicesByProvider,
            provider,
            nameof(ICodeReviewPublicationService));
    }

    public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider)
    {
        return GetRequired(this._reviewDiscoveryProvidersByProvider, provider, nameof(IReviewDiscoveryProvider));
    }

    public IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider)
    {
        return GetRequired(this._reviewerIdentityServicesByProvider, provider, nameof(IReviewerIdentityService));
    }

    public IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider)
    {
        return GetRequired(this._reviewAssignmentServicesByProvider, provider, nameof(IReviewAssignmentService));
    }

    public IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider)
    {
        return GetRequired(this._reviewThreadStatusWritersByProvider, provider, nameof(IReviewThreadStatusWriter));
    }

    public IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider)
    {
        return GetRequired(this._reviewThreadReplyPublishersByProvider, provider, nameof(IReviewThreadReplyPublisher));
    }

    public IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider)
    {
        return GetRequired(
            this._providerAdminDiscoveryServicesByProvider,
            provider,
            nameof(IProviderAdminDiscoveryService));
    }

    public IWebhookIngressService GetWebhookIngressService(ScmProvider provider)
    {
        return GetRequired(this._webhookIngressServicesByProvider, provider, nameof(IWebhookIngressService));
    }

    private static TService GetRequired<TService>(
        IReadOnlyDictionary<ScmProvider, TService> servicesByProvider,
        ScmProvider provider,
        string capabilityName)
        where TService : class
    {
        if (servicesByProvider.TryGetValue(provider, out var service))
        {
            return service;
        }

        throw new InvalidOperationException($"No {capabilityName} is registered for provider {provider}.");
    }

    private static void AddCapability<TService>(
        ICollection<string> capabilities,
        IReadOnlyDictionary<ScmProvider, TService> servicesByProvider,
        ScmProvider provider,
        string capabilityName)
        where TService : class
    {
        if (servicesByProvider.ContainsKey(provider))
        {
            capabilities.Add(capabilityName);
        }
    }
}
