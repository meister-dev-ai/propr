// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterProPR.Infrastructure.AI.Providers.AzureOpenAi;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace MeisterProPR.Infrastructure.AI.Providers.OpenAi;

/// <summary>
///     OpenAI-hosted provider driver.
/// </summary>
public sealed class OpenAiProviderDriver(OpenAiCompatibleTransport transport) : IAiProviderDriver
{
    public AiProviderKind ProviderKind => AiProviderKind.OpenAi;

    public async Task<AiModelDiscoveryResultDto> DiscoverModelsAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(options, ct);
        if ((int)result.StatusCode >= 400)
        {
            return new AiModelDiscoveryResultDto(
                "failed",
                true,
                [result.ErrorMessage ?? $"Provider discovery failed with status {(int)result.StatusCode}."],
                []);
        }

        var now = DateTimeOffset.UtcNow;
        return new AiModelDiscoveryResultDto(
            "succeeded",
            true,
            result.Models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : [],
            result.Models.Select(modelId => AzureOpenAiProviderDriver.GuessModelCapabilities(modelId, now)).ToList().AsReadOnly());
    }

    public async Task<AiVerificationResultDto> VerifyAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default)
    {
        var result = await transport.DiscoverModelsAsync(options, ct);
        if ((int)result.StatusCode >= 400)
        {
            return DriverFailureMapper.Failed(result.StatusCode, result.ErrorMessage);
        }

        List<string> warnings = result.Models.Count == 0
            ? ["No models were discovered from the provider. Manual model entry remains available."]
            : [];
        return DriverFailureMapper.Verified($"Verified OpenAI connectivity for '{options.BaseUrl}'.", warnings);
    }

    public IChatClient CreateChatClient(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        var options = CreateClientOptions(connection.BaseUrl);
        var credential = new ApiKeyCredential(connection.Secret ?? string.Empty);

        if (UsesResponsesApi(binding, model))
        {
            var client = new OpenAIClient(credential, options);
            return client.GetResponsesClient().AsIChatClient(model.RemoteModelId);
        }

        var chatClient = new ChatClient(model.RemoteModelId, credential, options);
        return chatClient.AsIChatClient();
    }

    public AgentReviewRuntimeCapabilities GetChatRuntimeCapabilities(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        _ = connection;
        var usesResponses = UsesResponsesApi(binding, model);
        return new AgentReviewRuntimeCapabilities(
            usesResponses,
            usesResponses,
            usesResponses,
            usesResponses);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        int dimensions)
    {
        _ = binding;
        _ = dimensions;

        var options = CreateClientOptions(connection.BaseUrl);
        var credential = new ApiKeyCredential(connection.Secret ?? string.Empty);
        var client = new EmbeddingClient(model.RemoteModelId, credential, options);
        return client.AsIEmbeddingGenerator();
    }

    private static OpenAIClientOptions CreateClientOptions(string baseUrl)
    {
        return new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl, UriKind.Absolute),
        };
    }

    private static bool UsesResponsesApi(AiPurposeBindingDto binding, AiConfiguredModelDto model)
    {
        return binding.ProtocolMode == AiProtocolMode.Responses
               || (binding.ProtocolMode == AiProtocolMode.Auto
                   && model.SupportedProtocolModes.Contains(AiProtocolMode.Responses));
    }
}
