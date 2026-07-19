// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Broker;

/// <summary>
///     ProPR-owned embedding broker backend used by internal ProPR broker endpoints.
/// </summary>
public sealed class LocalProPrEmbeddingBroker(IAiRuntimeResolver aiRuntimeResolver) : IProCursorEmbeddingBroker
{
    public async Task<ProCursorEmbeddingDeploymentDto> GetDeploymentAsync(
        Guid clientId,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        var runtime = await aiRuntimeResolver.ResolveEmbeddingRuntimeAsync(
            clientId,
            AiPurpose.EmbeddingDefault,
            expectedDimensions,
            ct);

        return new ProCursorEmbeddingDeploymentDto(
            runtime.Connection.Id,
            runtime.Model.RemoteModelId,
            runtime.Model.TokenizerName ?? string.Empty,
            runtime.Model.MaxInputTokens ?? 0,
            runtime.Model.EmbeddingDimensions ?? runtime.Dimensions,
            runtime.Model.InputCostPer1MUsd,
            runtime.Model.OutputCostPer1MUsd,
            runtime.Model.CachedInputCostPer1MUsd);
    }

    public async Task<ProCursorEmbeddingBatchResponse> GenerateEmbeddingsAsync(
        Guid clientId,
        IReadOnlyList<string> inputs,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return new ProCursorEmbeddingBatchResponse([]);
        }

        var runtime = await aiRuntimeResolver.ResolveEmbeddingRuntimeAsync(
            clientId,
            AiPurpose.EmbeddingDefault,
            expectedDimensions,
            ct);
        var result = await runtime.Generator.GenerateAsync(inputs, cancellationToken: ct);

        return new ProCursorEmbeddingBatchResponse(
            result.Select(item => item.Vector.ToArray()).ToList().AsReadOnly(),
            result.Usage?.InputTokenCount,
            result.Usage?.OutputTokenCount,
            result.Usage?.TotalTokenCount);
    }
}
