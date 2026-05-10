// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Service-side broker for ProPR-managed embedding access needed by the extracted ProCursor runtime.
/// </summary>
public interface IProCursorEmbeddingBroker
{
    /// <summary>
    ///     Resolves the active embedding deployment for a client.
    /// </summary>
    Task<ProCursorEmbeddingDeploymentDto> GetDeploymentAsync(
        Guid clientId,
        int? expectedDimensions = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Generates one embedding vector per input while preserving order.
    /// </summary>
    Task<ProCursorEmbeddingBatchResponse> GenerateEmbeddingsAsync(
        Guid clientId,
        IReadOnlyList<string> inputs,
        int? expectedDimensions = null,
        CancellationToken ct = default);
}
