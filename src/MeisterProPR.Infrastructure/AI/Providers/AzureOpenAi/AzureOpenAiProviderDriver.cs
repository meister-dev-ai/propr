// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI.Providers.AzureOpenAi;

/// <summary>
///     Azure OpenAI and Azure AI Foundry provider driver.
/// </summary>
public sealed class AzureOpenAiProviderDriver : IAiProviderDriver
{
    public AiProviderKind ProviderKind => AiProviderKind.AzureOpenAi;

    public async Task<AiModelDiscoveryResultDto> DiscoverModelsAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateAzureClient(options);
            var response = await client.GetOpenAIModelClient().GetModelsAsync(ct);
            var now = DateTimeOffset.UtcNow;

            var models = response.Value
                .Select(model => ToDiscoveredModel(model.Id, now))
                .ToList()
                .AsReadOnly();

            var warnings = models.Count == 0
                ? ["No models were discovered from the provider. Manual model entry remains available."]
                : Array.Empty<string>();

            return new AiModelDiscoveryResultDto("succeeded", true, warnings, models);
        }
        catch (ClientResultException exception)
        {
            return new AiModelDiscoveryResultDto("failed", true, [DriverFailureMapper.Failed(exception).Summary ?? exception.Message], []);
        }
        catch (Exception exception)
        {
            return new AiModelDiscoveryResultDto("failed", true, [DriverFailureMapper.Failed(exception).Summary ?? exception.Message], []);
        }
    }

    public async Task<AiVerificationResultDto> VerifyAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default)
    {
        try
        {
            var discovery = await this.DiscoverModelsAsync(options, ct);
            return DriverFailureMapper.Verified(
                $"Verified Azure OpenAI connectivity for '{options.BaseUrl}'.",
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
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        var client = CreateAzureClient(new AiConnectionProbeOptionsDto(
            connection.ProviderKind,
            connection.BaseUrl,
            connection.AuthMode,
            connection.Secret,
            connection.DefaultHeaders,
            connection.DefaultQueryParams));

        return binding.ProtocolMode == AiProtocolMode.ChatCompletions
            ? client.GetChatClient(model.RemoteModelId).AsIChatClient()
            : client.GetResponsesClient().AsIChatClient();
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        int dimensions)
    {
        _ = binding;
        _ = dimensions;

        var client = CreateAzureClient(new AiConnectionProbeOptionsDto(
            connection.ProviderKind,
            connection.BaseUrl,
            connection.AuthMode,
            connection.Secret,
            connection.DefaultHeaders,
            connection.DefaultQueryParams));

        return client.GetEmbeddingClient(model.RemoteModelId).AsIEmbeddingGenerator();
    }

    private static AzureOpenAIClient CreateAzureClient(AiConnectionProbeOptionsDto options)
    {
        var endpoint = NormalizeRoot(options.BaseUrl);
        var clientOptions = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };

        return options.AuthMode == AiAuthMode.AzureIdentity
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), clientOptions)
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.Secret ?? string.Empty), clientOptions);
    }

    private static Uri NormalizeRoot(string endpointUrl)
    {
        var uri = new Uri(endpointUrl);
        return new Uri($"{uri.Scheme}://{uri.Host}/");
    }

    private static AiConfiguredModelDto ToDiscoveredModel(string remoteModelId, DateTimeOffset discoveredAt)
    {
        return GuessModelCapabilities(remoteModelId, discoveredAt);
    }

    internal static AiConfiguredModelDto GuessModelCapabilities(string remoteModelId, DateTimeOffset discoveredAt)
    {
        var normalized = remoteModelId.Trim();
        var isEmbedding = normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase);

        if (isEmbedding)
        {
            return new AiConfiguredModelDto(
                Guid.Empty,
                normalized,
                normalized,
                [AiOperationKind.Embedding],
                [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
                "cl100k_base",
                8192,
                1536,
                false,
                false,
                AiConfiguredModelSource.Discovered,
                discoveredAt,
                0,
                0);
        }

        return new AiConfiguredModelDto(
            Guid.Empty,
            normalized,
            normalized,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
            null,
            null,
            null,
            true,
            true,
            AiConfiguredModelSource.Discovered,
            discoveredAt);
    }
}
