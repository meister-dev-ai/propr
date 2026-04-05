// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Generates semantic embeddings and AI-generated resolution summaries for PR review threads.
///     Implemented in Infrastructure using <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}" />
///     and <see cref="Microsoft.Extensions.AI.IChatClient" /> — no concrete provider SDK references above Infrastructure.
/// </summary>
public interface IThreadMemoryEmbedder
{
    /// <summary>
    ///     Generates a float vector for the given composite text using the client's embedding model.
    /// </summary>
    /// <param name="compositeText">Pre-built composite string combining file path, change excerpt, comments, and summary.</param>
    /// <param name="clientId">The client whose embedding model configuration applies.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The embedding vector as <c>float[]</c>.</returns>
    /// <exception cref="InvalidOperationException">
    ///     When no <c>Embedding</c>-category AI connection is configured for the client.
    /// </exception>
    Task<float[]> GenerateEmbeddingAsync(string compositeText, Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Generates a 2–4 sentence AI summary describing how and why the given thread was resolved.
    ///     Never throws — returns a placeholder summary on failure.
    /// </summary>
    /// <param name="filePath">File the thread was anchored to (null for PR-level threads).</param>
    /// <param name="changeExcerpt">Diff excerpt relevant to the thread.</param>
    /// <param name="commentHistory">Full comment history of the thread.</param>
    /// <param name="clientId">The client whose active AI connection should be used for summary generation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A non-empty resolution summary string.</returns>
    Task<string> GenerateResolutionSummaryAsync(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        Guid clientId,
        CancellationToken ct = default);
}
