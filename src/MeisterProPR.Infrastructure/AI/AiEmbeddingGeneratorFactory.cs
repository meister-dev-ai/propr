// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Creates <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> instances targeting an Azure OpenAI
///     or AI Foundry embedding endpoint. Implements <see cref="IAiEmbeddingGeneratorFactory" />.
/// </summary>
public sealed class AiEmbeddingGeneratorFactory : IAiEmbeddingGeneratorFactory
{
    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(
        string endpointUrl,
        string deploymentName,
        string? apiKey,
        int dimensions)
    {
        var uri = new Uri(endpointUrl);

        // Azure AI Foundry portal URLs include a project path (.../api/projects/{project})
        // that is not part of the Azure OpenAI API surface — use only the resource root.
        if (uri.Host.EndsWith("services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri($"{uri.Scheme}://{uri.Host}/");
        }

        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential())
            : new AzureOpenAIClient(uri, new ApiKeyCredential(apiKey));

        // dimensions is stored in configuration for pgvector column sizing and is applied at
        // query time via EmbeddingGenerationOptions; the underlying client uses its model default.
        _ = dimensions;

        return azureClient
            .GetEmbeddingClient(deploymentName)
            .AsIEmbeddingGenerator();
    }
}
