// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Generates embeddings for ProCursor knowledge chunks.
/// </summary>
public interface IProCursorEmbeddingService
{
    /// <summary>
    ///     Validates that the client has a usable embedding deployment for ProCursor indexing.
    /// </summary>
    Task EnsureConfigurationAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Splits oversized chunks into smaller embedding-safe chunks while preserving order.
    /// </summary>
    Task<IReadOnlyList<ProCursorExtractedChunk>> NormalizeChunksAsync(
        Guid clientId,
        IReadOnlyList<ProCursorExtractedChunk> chunks,
        CancellationToken ct = default);

    /// <summary>Generates one embedding vector per input text, preserving order.</summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        Guid clientId,
        IReadOnlyList<string> inputs,
        ProCursorEmbeddingUsageContext? usageContext = null,
        CancellationToken ct = default);
}
