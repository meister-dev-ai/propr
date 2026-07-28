// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     OpenAI-hosted provider driver.
/// </summary>
/// <param name="allowPrivateEgress">When true (Development, or the operator opt-in), the probe-target check permits private/loopback hosts.</param>
/// <param name="allowInsecureScheme">When true (Development only), the probe-target check permits a plain-http baseUrl.</param>
public sealed class OpenAiProviderDriver(
    OpenAiCompatibleTransport transport,
    IHttpClientFactory httpClientFactory,
    bool allowPrivateEgress,
    bool allowInsecureScheme) : IAiProviderDriver
{
    public AiProviderKind ProviderKind => AiProviderKind.OpenAi;

    /// <inheritdoc />
    public IReadOnlyList<AiProtocolMode> SupportedProtocolModes => AiProtocolModeSupport.OpenAiFamily;

    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        // Azure-hosted endpoints are refused rather than accepted here: they authenticate differently, and the
        // Azure driver is the one that can use a managed identity instead of a key.
        return OpenAiCompatibleProbeRules.Validate(
            target,
            allowPrivateEgress,
            allowInsecureScheme,
            rejectAzureHosts: true);
    }

    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(endpoint, ct);
        if ((int)result.StatusCode >= 400)
        {
            return new ProviderModelDiscoveryResult(
                "failed",
                true,
                [result.ErrorMessage ?? $"Provider discovery failed with status {(int)result.StatusCode}."],
                []);
        }

        return new ProviderModelDiscoveryResult(
            "succeeded",
            true,
            result.Models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : [],
            result.Models.Select(modelId => AzureOpenAiProviderDriver.GuessModelCapabilities(modelId)).ToList().AsReadOnly());
    }

    public async Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(endpoint, ct);
        if ((int)result.StatusCode >= 400)
        {
            return DriverFailureMapper.Failed(result.StatusCode, result.ErrorMessage);
        }

        List<string> warnings = result.Models.Count == 0
            ? ["No models were discovered from the provider. Manual model entry remains available."]
            : [];
        return DriverFailureMapper.Verified($"Verified OpenAI connectivity for '{endpoint.BaseUrl}'.", warnings);
    }

    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        AiProtocolModeSupport.Require(endpoint.ProviderKind, this.SupportedProtocolModes, protocolMode);

        var clientOptions = this.CreateClientOptions(endpoint.BaseUrl);
        var credential = new ApiKeyCredential(endpoint.Secret ?? string.Empty);

        if (UsesResponsesApi(protocolMode, model))
        {
            var client = new OpenAIClient(credential, clientOptions);
            return client.GetResponsesClient().AsIChatClient(model.RemoteModelId);
        }

        var chatClient = new ChatClient(model.RemoteModelId, credential, clientOptions);
        return chatClient.AsIChatClient();
    }

    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        _ = endpoint;
        var usesResponses = UsesResponsesApi(protocolMode, model);
        return new ProviderRuntimeCapabilities(
            usesResponses,
            usesResponses,
            usesResponses,
            usesResponses);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        AiProtocolModeSupport.Require(endpoint.ProviderKind, this.SupportedProtocolModes, protocolMode);
        _ = dimensions;

        var clientOptions = this.CreateClientOptions(endpoint.BaseUrl);
        var credential = new ApiKeyCredential(endpoint.Secret ?? string.Empty);
        var client = new EmbeddingClient(model.RemoteModelId, credential, clientOptions);
        return client.AsIEmbeddingGenerator();
    }

    private OpenAIClientOptions CreateClientOptions(string baseUrl)
    {
        // Route runtime traffic through the SSRF-guarded HttpClient so a saved baseUrl that resolves to an
        // internal address cannot be reached at review time (the connect-time guard validates the resolved IP).
        return new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient("AiProviderRuntime")),
        };
    }

    private static bool UsesResponsesApi(AiProtocolMode protocolMode, ProviderModelDescriptor model)
    {
        return protocolMode == AiProtocolMode.Responses
               || (protocolMode == AiProtocolMode.Auto
                   && model.SupportedProtocolModes.Contains(AiProtocolMode.Responses));
    }
}
