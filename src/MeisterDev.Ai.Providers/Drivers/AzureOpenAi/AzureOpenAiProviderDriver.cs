// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Azure OpenAI and Azure AI Foundry provider driver.
/// </summary>
public sealed class AzureOpenAiProviderDriver : IAiProviderDriver
{
    public AiProviderKind ProviderKind => AiProviderKind.AzureOpenAi;

    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        return AiProbeTargetValidation.ForAzureOpenAi(target);
    }

    public async Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateAzureClient(endpoint);
            var response = await client.GetOpenAIModelClient().GetModelsAsync(ct);

            var models = response.Value
                .Select(model => ToDiscoveredModel(model.Id))
                .ToList()
                .AsReadOnly();

            var warnings = models.Count == 0
                ? ["No models were discovered from the provider. Manual model entry remains available."]
                : Array.Empty<string>();

            return new ProviderModelDiscoveryResult("succeeded", true, warnings, models);
        }
        catch (ClientResultException exception)
        {
            return new ProviderModelDiscoveryResult("failed", true, [DriverFailureMapper.Failed(exception).Summary ?? exception.Message], []);
        }
        catch (Exception exception)
        {
            return new ProviderModelDiscoveryResult("failed", true, [DriverFailureMapper.Failed(exception).Summary ?? exception.Message], []);
        }
    }

    public async Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        try
        {
            var discovery = await this.DiscoverModelsAsync(endpoint, ct);
            return DriverFailureMapper.Verified(
                $"Verified Azure OpenAI connectivity for '{endpoint.BaseUrl}'.",
                discovery.Warnings);
        }
        catch (ClientResultException exception)
        {
            return DriverFailureMapper.Failed(exception);
        }
        catch (Exception exception)
        {
            return DriverFailureMapper.Failed(exception);
        }
    }

    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        var client = CreateAzureClient(
            new ProviderEndpoint(
                endpoint.ProviderKind,
                endpoint.BaseUrl,
                endpoint.AuthMode,
                endpoint.Secret,
                endpoint.DefaultHeaders,
                endpoint.DefaultQueryParams));

        return protocolMode == AiProtocolMode.ChatCompletions
            ? client.GetChatClient(model.RemoteModelId).AsIChatClient()
            : client.GetResponsesClient().AsIChatClient();
    }

    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode)
    {
        _ = endpoint;
        _ = model;

        var usesResponses = protocolMode != AiProtocolMode.ChatCompletions;
        return new ProviderRuntimeCapabilities(
            usesResponses,
            usesResponses,
            usesResponses,
            usesResponses,
            true,
            true);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        AiProtocolMode protocolMode,
        int dimensions)
    {
        _ = protocolMode;
        _ = dimensions;

        var client = CreateAzureClient(
            new ProviderEndpoint(
                endpoint.ProviderKind,
                endpoint.BaseUrl,
                endpoint.AuthMode,
                endpoint.Secret,
                endpoint.DefaultHeaders,
                endpoint.DefaultQueryParams));

        return client.GetEmbeddingClient(model.RemoteModelId).AsIEmbeddingGenerator();
    }

    private static AzureOpenAIClient CreateAzureClient(ProviderEndpoint endpoint)
    {
        var root = NormalizeRoot(endpoint.BaseUrl);
        var clientOptions = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };

        return endpoint.AuthMode == AiAuthMode.AzureIdentity
            ? new AzureOpenAIClient(root, new DefaultAzureCredential(), clientOptions)
            : new AzureOpenAIClient(root, new ApiKeyCredential(endpoint.Secret ?? string.Empty), clientOptions);
    }

    private static Uri NormalizeRoot(string endpointUrl)
    {
        var uri = new Uri(endpointUrl);
        return new Uri($"{uri.Scheme}://{uri.Host}/");
    }

    private static ProviderDiscoveredModel ToDiscoveredModel(string remoteModelId)
    {
        return GuessModelCapabilities(remoteModelId);
    }

    internal static ProviderDiscoveredModel GuessModelCapabilities(string remoteModelId)
    {
        var normalized = remoteModelId.Trim();
        var isEmbedding = normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase);

        if (isEmbedding)
        {
            return new ProviderDiscoveredModel(
                normalized,
                normalized,
                [AiOperationKind.Embedding],
                [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
                "cl100k_base",
                MaxInputTokens: 8192,
                MaxContextTokens: null,
                EmbeddingDimensions: 1536);
        }

        return new ProviderDiscoveredModel(
            normalized,
            normalized,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
            SupportsStructuredOutput: true,
            SupportsToolUse: true);
    }
}
