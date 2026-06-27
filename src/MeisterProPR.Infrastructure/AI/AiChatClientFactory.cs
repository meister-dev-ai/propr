// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Creates <see cref="IChatClient" /> instances targeting the OpenAI Responses API. Azure OpenAI and Azure
///     AI Foundry endpoints (<c>*.openai.azure.com</c>, <c>*.services.ai.azure.com</c>) are reached through the
///     Azure SDK with optional API key or <see cref="DefaultAzureCredential" /> auth; OpenAI-compatible endpoints
///     (plain OpenAI or a LiteLLM proxy) are reached through the OpenAI SDK pointed at the configured base URL.
///     Either way the returned client is endpoint-bound and selects the deployment per request via
///     <see cref="ChatOptions.ModelId" />.
/// </summary>
public sealed partial class AiChatClientFactory(ILogger<AiChatClientFactory> logger) : IAiChatClientFactory
{
    /// <inheritdoc />
    public IChatClient CreateClient(string endpointUrl, string? apiKey)
    {
        return this.CreateClient(endpointUrl, apiKey, AiProviderKind.AzureOpenAi);
    }

    /// <inheritdoc />
    public IChatClient CreateClient(string endpointUrl, string? apiKey, AiProviderKind provider)
    {
        // Reasoning models can take several minutes to generate a response.
        // The default NetworkTimeout of 100 s is too short — raise it to 10 min.
        var networkTimeout = TimeSpan.FromMinutes(10);

        if (provider is AiProviderKind.OpenAi or AiProviderKind.LiteLlm)
        {
            var openAiOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpointUrl, UriKind.Absolute),
                NetworkTimeout = networkTimeout,
            };
            var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey ?? string.Empty), openAiOptions);
            return openAiClient.GetResponsesClient().AsIChatClient();
        }

        var uri = NormaliseRoot(endpointUrl);
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = networkTimeout,
        };

        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential(), options)
            : new AzureOpenAIClient(uri, new ApiKeyCredential(apiKey), options);

        return azureClient.GetResponsesClient().AsIChatClient();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ProbeDeploymentsAsync(
        string endpointUrl,
        string apiKey,
        CancellationToken ct = default)
    {
        var endpoint = new Uri(endpointUrl);
        var azureClient = new AzureOpenAIClient(
            endpoint,
            new ApiKeyCredential(apiKey),
            new AzureOpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(30),
            });

        var modelClient = azureClient.GetOpenAIModelClient();

        try
        {
            LogProbingDeploymentList(logger, endpointUrl);

            var models = await modelClient.GetModelsAsync(ct);

            return models.Value.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        }
        catch
        {
            return [];
        }
    }

    // Azure AI Foundry portal URLs include a project path (.../api/projects/{project})
    // that is not part of the Azure OpenAI API surface — strip back to the resource root.
    private static Uri NormaliseRoot(string endpointUrl)
    {
        var uri = new Uri(endpointUrl);
        return uri.Host.EndsWith("services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"{uri.Scheme}://{uri.Host}/")
            : new Uri($"{uri.Scheme}://{uri.Host}/");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "AiChatClientFactory: probing deployments at {RequestUri}")]
    private static partial void LogProbingDeploymentList(ILogger logger, string requestUri);

    private sealed record DeploymentsListResponse(DeploymentItem[]? Value);

    private sealed record DeploymentItem(string Id);
}
