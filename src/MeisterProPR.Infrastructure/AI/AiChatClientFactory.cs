// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Net.Http.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Creates <see cref="IChatClient"/> instances targeting the Azure OpenAI Responses API.
///     Supports both <c>*.openai.azure.com</c> and <c>*.services.ai.azure.com</c> (Azure AI Foundry)
///     endpoints with optional API key or <see cref="DefaultAzureCredential"/> auth.
/// </summary>
public sealed partial class AiChatClientFactory(ILogger<AiChatClientFactory> logger) : IAiChatClientFactory
{
    /// <inheritdoc />
    public IChatClient CreateClient(string endpointUrl, string? apiKey)
    {
        var uri = NormaliseRoot(endpointUrl);

        // Reasoning models can take several minutes to generate a response.
        // The default NetworkTimeout of 100 s is too short — raise it to 10 min.
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };

        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential(), options)
            : new AzureOpenAIClient(uri, new ApiKeyCredential(apiKey), options);

        return azureClient.GetResponsesClient().AsIChatClient();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ProbeDeploymentsAsync(string endpointUrl, string apiKey, CancellationToken ct = default)
    {
        var endpoint = new Uri(endpointUrl);
        var azureClient = new AzureOpenAIClient(
            endpoint,
            new ApiKeyCredential(apiKey),
            new AzureOpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(30)
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

    private sealed record DeploymentsListResponse(DeploymentItem[]? Value);

    private sealed record DeploymentItem(string Id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AiChatClientFactory: probing deployments at {RequestUri}")]
    private static partial void LogProbingDeploymentList(ILogger logger, string requestUri);
}
