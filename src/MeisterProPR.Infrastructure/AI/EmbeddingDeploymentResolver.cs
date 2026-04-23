// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Resolves and validates the embedding deployment configuration used at runtime.
/// </summary>
public sealed class EmbeddingDeploymentResolver(
    IAiConnectionRepository aiConnectionRepository,
    IAiRuntimeResolver? aiRuntimeResolver = null)
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
        _ = allowDefaultFallback;

        if (aiRuntimeResolver is not null)
        {
            var runtime = await aiRuntimeResolver.ResolveEmbeddingRuntimeAsync(
                clientId,
                AiPurpose.EmbeddingDefault,
                expectedDimensions,
                ct);

            return this.Resolve(runtime.Connection, runtime.Model.RemoteModelId, expectedDimensions);
        }

        var connection = await aiConnectionRepository.GetForTierAsync(clientId, AiConnectionModelCategory.Embedding, ct);

        if (connection is null)
        {
            throw new InvalidOperationException("no_embedding_connection_configured");
        }

        var deploymentName = connection.GetBoundModelId(AiPurpose.EmbeddingDefault)
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

        var existingModel = connection.ConfiguredModels.FirstOrDefault(candidate =>
            string.Equals(candidate.RemoteModelId, deploymentName, StringComparison.OrdinalIgnoreCase));

        if (existingModel is null)
        {
            throw new InvalidOperationException(
                $"Embedding deployment '{deploymentName}' configured for AI connection '{connection.DisplayName}' was not found among the connection's configured models.");
        }

        var capability = new AiConnectionModelCapabilityDto(
                existingModel.RemoteModelId,
                existingModel.TokenizerName!,
                existingModel.MaxInputTokens ?? 8192,
                existingModel.EmbeddingDimensions ?? 1536,
                existingModel.InputCostPer1MUsd,
                existingModel.OutputCostPer1MUsd);

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

        var model = existingModel is not null &&
                    !string.IsNullOrWhiteSpace(existingModel.TokenizerName) &&
                    existingModel.MaxInputTokens.HasValue &&
                    existingModel.EmbeddingDimensions.HasValue
            ? existingModel
            : new AiConfiguredModelDto(
                existingModel?.Id ?? Guid.Empty,
                deploymentName,
                existingModel?.DisplayName ?? deploymentName,
                [AiOperationKind.Embedding],
                [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
                capability.TokenizerName,
                capability.MaxInputTokens,
                capability.EmbeddingDimensions,
                false,
                false,
                existingModel?.Source ?? AiConfiguredModelSource.KnownCatalog,
                existingModel?.LastSeenAt,
                capability.InputCostPer1MUsd,
                capability.OutputCostPer1MUsd);

        return new ValidatedEmbeddingDeployment(connection, model);
    }
}
