// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     LiteLLM OpenAI-compatible provider driver.
/// </summary>
public sealed class LiteLlmProviderDriver(
    OpenAiCompatibleTransport transport,
    IHttpClientFactory httpClientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    private readonly OpenAiProviderDriver _innerDriver = new(transport, httpClientFactory, allowPrivateEgress, allowInsecureScheme);

    public AiProviderKind ProviderKind => AiProviderKind.LiteLlm;

    /// <inheritdoc />
    public IReadOnlyList<AiProtocolMode> SupportedProtocolModes => AiProtocolModeSupport.OpenAiFamily;

    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        // LiteLLM is a generic OpenAI-compatible proxy, so (unlike plain OpenAI) an Azure host is not rejected.
        return AiProbeTargetValidation.ForOpenAiCompatible(target, allowPrivateEgress, allowInsecureScheme, rejectAzureHosts: false);
    }

    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return this._innerDriver.DiscoverModelsAsync(endpoint with { ProviderKind = AiProviderKind.LiteLlm }, ct);
    }

    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return this._innerDriver.VerifyAsync(endpoint with { ProviderKind = AiProviderKind.LiteLlm }, ct);
    }

    public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, AiProtocolMode protocolMode)
    {
        return this._innerDriver.CreateChatClient(endpoint with { ProviderKind = AiProviderKind.LiteLlm }, model, protocolMode);
    }

    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        _ = endpoint;
        _ = model;
        _ = protocolMode;

        return new ProviderRuntimeCapabilities(
            false,
            false,
            false,
            false);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        return this._innerDriver.CreateEmbeddingGenerator(endpoint with { ProviderKind = AiProviderKind.LiteLlm }, model, protocolMode, dimensions);
    }
}
