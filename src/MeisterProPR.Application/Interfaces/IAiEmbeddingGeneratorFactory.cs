// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Factory for creating <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> instances from per-client AI
///     connection details. Analogous to <see cref="IAiChatClientFactory" /> for chat clients.
/// </summary>
public interface IAiEmbeddingGeneratorFactory
{
    /// <summary>
    ///     Creates an <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> for the given endpoint and deployment.
    /// </summary>
    /// <param name="endpointUrl">The Azure OpenAI or AI Foundry endpoint URL.</param>
    /// <param name="deploymentName">The embedding model deployment name (e.g. <c>text-embedding-3-small</c>).</param>
    /// <param name="apiKey">Optional API key. When null <c>DefaultAzureCredential</c> is used.</param>
    /// <param name="dimensions">
    ///     Expected vector dimension. Must match the pgvector column width configured at migration time.
    ///     Passed to the generator for validation; the underlying model default is used if the provider ignores this.
    /// </param>
    /// <returns>A configured <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> instance.</returns>
    IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(
        string endpointUrl,
        string deploymentName,
        string? apiKey,
        int dimensions);
}
