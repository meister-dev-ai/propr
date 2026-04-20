// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Resolves and validates the embedding deployment configuration used at runtime.
/// </summary>
public sealed class EmbeddingDeploymentResolver(IAiConnectionRepository aiConnectionRepository)
{
    /// <summary>
    ///     Resolves the embedding deployment for the given client and validates that the selected
    ///     deployment has complete capability metadata compatible with the expected vector width.
    /// </summary>
    public async Task<ValidatedEmbeddingDeployment> ResolveForClientAsync(
        Guid clientId,
        int expectedDimensions,
        bool allowDefaultFallback,
        CancellationToken ct = default)
    {
        var connection = await aiConnectionRepository.GetForTierAsync(
            clientId,
            AiConnectionModelCategory.Embedding,
            ct);

        if (connection is null && allowDefaultFallback)
        {
            connection = await aiConnectionRepository.GetActiveForClientAsync(clientId, ct);
        }

        if (connection is null)
        {
            throw new InvalidOperationException("no_embedding_connection_configured");
        }

        var deploymentName = connection.ActiveModel
                             ?? connection.Models.FirstOrDefault()
                             ?? throw new InvalidOperationException("no_embedding_model_configured");

        return this.Resolve(connection, deploymentName, expectedDimensions);
    }

    /// <summary>
    ///     Validates a specific deployment selection on an AI connection.
    /// </summary>
    public ValidatedEmbeddingDeployment Resolve(
        AiConnectionDto connection,
        string deploymentName,
        int expectedDimensions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var capability = (connection.ModelCapabilities ?? [])
            .FirstOrDefault(candidate =>
                string.Equals(candidate.ModelName, deploymentName, StringComparison.OrdinalIgnoreCase));

        capability ??= KnownEmbeddingModelCapabilities.Get(deploymentName);

        if (capability is null)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{deploymentName}' on AI connection '{connection.DisplayName}' is missing capability metadata.");
        }

        if (!EmbeddingTokenizerRegistry.IsSupported(capability.TokenizerName))
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{deploymentName}' on AI connection '{connection.DisplayName}' uses unsupported tokenizer '{capability.TokenizerName}'.");
        }

        if (capability.MaxInputTokens <= 0)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{deploymentName}' on AI connection '{connection.DisplayName}' has invalid max input tokens '{capability.MaxInputTokens}'.");
        }

        if (capability.EmbeddingDimensions != expectedDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{deploymentName}' on AI connection '{connection.DisplayName}' returns {capability.EmbeddingDimensions} dimensions, but {expectedDimensions} are required.");
        }

        return new ValidatedEmbeddingDeployment(connection, deploymentName, capability);
    }
}
