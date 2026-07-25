// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Driver for any OpenAI-compatible endpoint reached at an operator-supplied base URL — a vendor API, an
///     aggregator, or a self-hosted server. The wire protocol is the OpenAI shape, so the transport work is
///     delegated to <see cref="OpenAiProviderDriver" />; what this class contributes is the profile's own rules.
///     Unlike plain OpenAI it does not reject an Azure host, because an operator may legitimately front an Azure
///     deployment with a compatible gateway, and unlike <see cref="AiProviderKind.LiteLlm" /> it describes a
///     direct endpoint rather than a proxy, which keeps the two separable in configuration, telemetry, and the
///     per-provider usage key map.
/// </summary>
public sealed class OpenAiCompatibleProviderDriver(
    OpenAiCompatibleTransport transport,
    IHttpClientFactory httpClientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    private readonly OpenAiProviderDriver _innerDriver = new(transport, httpClientFactory, allowPrivateEgress, allowInsecureScheme);

    public AiProviderKind ProviderKind => AiProviderKind.OpenAiCompatible;

    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        // The egress rules are the point of this profile: an operator sets the base URL, so it is validated
        // against the private-address and scheme policy exactly as any other operator-supplied endpoint.
        return AiProbeTargetValidation.ForOpenAiCompatible(
            target,
            allowPrivateEgress,
            allowInsecureScheme,
            rejectAzureHosts: false);
    }

    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return this._innerDriver.DiscoverModelsAsync(this.Rekeyed(endpoint), ct);
    }

    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return this._innerDriver.VerifyAsync(this.Rekeyed(endpoint), ct);
    }

    public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, AiProtocolMode protocolMode)
    {
        return this._innerDriver.CreateChatClient(this.Rekeyed(endpoint), model, protocolMode);
    }

    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        _ = endpoint;
        _ = model;
        _ = protocolMode;

        // A compatible endpoint is assumed to offer the chat-completions surface only. Provider-managed
        // sessions, background responses, and prompt caching are OpenAI-specific affordances an arbitrary
        // compatible server cannot be assumed to implement, so nothing is claimed on its behalf.
        return ProviderRuntimeCapabilities.None;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        return this._innerDriver.CreateEmbeddingGenerator(this.Rekeyed(endpoint), model, protocolMode, dimensions);
    }

    // The inner driver keys behaviour off the provider kind, so it is rewritten before delegating.
    private ProviderEndpoint Rekeyed(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint with { ProviderKind = this.ProviderKind };
    }
}
