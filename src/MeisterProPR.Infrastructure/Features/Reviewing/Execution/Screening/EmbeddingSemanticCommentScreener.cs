// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;

/// <summary>
///     Embedding-based <see cref="ISemanticCommentScreener" />: classifies a comment by cosine similarity to
///     per-class exemplar centroids computed over the client's embedding model (<see cref="AiPurpose.EmbeddingDefault" />,
///     the same runtime the thread-memory embedder uses). Language-robust because the embedding model is
///     multilingual — English exemplars catch equivalent hedging in any language. Degraded-safe: no bound
///     embedding model, an empty exemplar set, or any failure returns <see cref="CommentScreeningResult.Firm" />
///     so the comment is kept. Exemplar centroids are a one-time setup per embedding model, cached by remote
///     model id, so a review embeds only the comment under test on each call.
/// </summary>
public sealed partial class EmbeddingSemanticCommentScreener(
    IOptions<AiReviewOptions> options,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    ILogger<EmbeddingSemanticCommentScreener>? logger = null) : ISemanticCommentScreener
{
    private const int MaxCommentChars = 2000;

    private readonly ConcurrentDictionary<string, IReadOnlyList<ClassCentroid>> _centroidCache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<CommentScreeningResult> ClassifyAsync(string commentText, Guid clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commentText))
        {
            return CommentScreeningResult.Firm;
        }

        if (aiRuntimeResolver is null || clientId == Guid.Empty)
        {
            return CommentScreeningResult.DegradedFirm;
        }

        try
        {
            var runtime = await aiRuntimeResolver
                .ResolveEmbeddingRuntimeAsync(clientId, AiPurpose.EmbeddingDefault, options.Value.MemoryEmbeddingDimensions, ct)
                .ConfigureAwait(false);

            var centroids = await this.GetCentroidsAsync(runtime, ct).ConfigureAwait(false);
            if (centroids.Count == 0)
            {
                return CommentScreeningResult.DegradedFirm;
            }

            var vector = await EmbedAsync(runtime, Truncate(commentText), ct).ConfigureAwait(false);
            Normalize(vector);

            var best = CommentScreeningClass.Firm;
            var bestSimilarity = double.NegativeInfinity;
            foreach (var centroid in centroids)
            {
                var similarity = Dot(vector, centroid.Vector);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = centroid.Class;
                }
            }

            // A Firm winner, or a Hedged/Vague winner below the confidence threshold, keeps the comment. Only a
            // confident hedged/vague match screens it — conservative by construction on an uncertain classification.
            if (best == CommentScreeningClass.Firm || bestSimilarity < options.Value.CommentScreeningSimilarityThreshold)
            {
                return new CommentScreeningResult(CommentScreeningClass.Firm, Math.Max(0.0, bestSimilarity));
            }

            return new CommentScreeningResult(best, bestSimilarity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogScreeningFailed(logger, ex);
            return CommentScreeningResult.DegradedFirm;
        }
    }

    // Embeds every exemplar for the runtime's model in one batched call, averages per class into a normalized
    // centroid, and caches the set by remote model id. A concurrent recompute is harmless (idempotent result).
    private async Task<IReadOnlyList<ClassCentroid>> GetCentroidsAsync(IResolvedAiEmbeddingRuntime runtime, CancellationToken ct)
    {
        if (this._centroidCache.TryGetValue(runtime.Model.RemoteModelId, out var cached))
        {
            return cached;
        }

        var labeled = CommentScreeningExemplars.ByClass
            .SelectMany(entry => entry.Value.Select(text => (entry.Key, text)))
            .ToList();
        if (labeled.Count == 0)
        {
            return [];
        }

        var embeddings = await runtime.Generator
            .GenerateAsync(labeled.Select(item => item.text).ToList(), cancellationToken: ct)
            .ConfigureAwait(false);

        var centroids = labeled
            .Select((item, index) => (item.Key, Vector: embeddings[index].Vector.ToArray()))
            .GroupBy(pair => pair.Key)
            .Select(group =>
            {
                var centroid = Average(group.Select(pair => pair.Vector).ToList());
                Normalize(centroid);
                return new ClassCentroid(group.Key, centroid);
            })
            .ToList();

        this._centroidCache.TryAdd(runtime.Model.RemoteModelId, centroids);
        return centroids;
    }

    private static async Task<float[]> EmbedAsync(IResolvedAiEmbeddingRuntime runtime, string text, CancellationToken ct)
    {
        var result = await runtime.Generator.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        return result[0].Vector.ToArray();
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxCommentChars ? trimmed : trimmed[..MaxCommentChars];
    }

    private static float[] Average(IReadOnlyList<float[]> vectors)
    {
        var dimensions = vectors[0].Length;
        var sum = new float[dimensions];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < dimensions && i < vector.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        for (var i = 0; i < dimensions; i++)
        {
            sum[i] /= vectors.Count;
        }

        return sum;
    }

    private static void Normalize(float[] vector)
    {
        var sumOfSquares = 0.0;
        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        var magnitude = Math.Sqrt(sumOfSquares);
        if (magnitude <= 0.0)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }
    }

    // Both operands are unit-normalized, so the dot product is the cosine similarity.
    private static double Dot(float[] first, float[] second)
    {
        var length = Math.Min(first.Length, second.Length);
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            sum += (double)first[i] * second[i];
        }

        return sum;
    }

    private sealed record ClassCentroid(CommentScreeningClass Class, float[] Vector);
}
