// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using MeisterProPR.Application.DTOs;
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
        return (int)result.StatusCode >= 400
            ? DriverFailureMapper.Failed(result.StatusCode, result.ErrorMessage)
            : DriverFailureMapper.Verified($"Verified OpenAI connectivity for '{options.BaseUrl}'.",
                result.Models.Count == 0 ? ["No models were discovered from the provider. Manual model entry remains available."] : []);
    }

    public IChatClient CreateChatClient(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        var options = CreateClientOptions(connection.BaseUrl);
        var credential = new ApiKeyCredential(connection.Secret ?? string.Empty);
        var client = new ChatClient(model.RemoteModelId, credential, options);
        return client.AsIChatClient();
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
}
