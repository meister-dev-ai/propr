// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;
using MeisterProPR.Infrastructure.Tests.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Screening;

/// <summary>
///     Mechanism tests for <see cref="EmbeddingSemanticCommentScreener" /> using a fake embedding generator
///     (no live model, no spend). Each class's exemplars map to an orthonormal direction so the per-class
///     centroids are those directions; a comment is then placed near a chosen centroid to assert the cosine
///     classification, the confidence-threshold gate, centroid caching, and the degraded-safe paths.
/// </summary>
public sealed class EmbeddingSemanticCommentScreenerTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009");

    // Orthonormal direction per class; exemplar centroids collapse to these.
    private static float[] Direction(CommentScreeningClass screeningClass)
    {
        return screeningClass switch
        {
            CommentScreeningClass.Hedged => [1f, 0f, 0f],
            CommentScreeningClass.Vague => [0f, 1f, 0f],
            _ => [0f, 0f, 1f],
        };
    }

    [Fact]
    public async Task ClassifyAsync_CommentOnHedgedCentroid_ClassifiesHedged()
    {
        var screener = BuildScreener(out _, overrides: new Dictionary<string, float[]> { ["comment"] = [1f, 0f, 0f] });

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.False(result.IsDegraded);
        Assert.Equal(CommentScreeningClass.Hedged, result.Class);
    }

    [Fact]
    public async Task ClassifyAsync_CommentOnVagueCentroid_ClassifiesVague()
    {
        var screener = BuildScreener(out _, overrides: new Dictionary<string, float[]> { ["comment"] = [0f, 1f, 0f] });

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.Equal(CommentScreeningClass.Vague, result.Class);
    }

    [Fact]
    public async Task ClassifyAsync_CommentOnFirmCentroid_ReturnsFirm()
    {
        var screener = BuildScreener(out _, overrides: new Dictionary<string, float[]> { ["comment"] = [0f, 0f, 1f] });

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.Equal(CommentScreeningClass.Firm, result.Class);
    }

    [Fact]
    public async Task ClassifyAsync_HedgedWinnerBelowThreshold_KeptAsFirm()
    {
        // The comment leans hedged but the cosine (~0.82) is under the configured 0.9 threshold, so the
        // conservative rule keeps it as Firm rather than screening it on an uncertain match.
        var screener = BuildScreener(out _, 0.9, new Dictionary<string, float[]> { ["comment"] = [2f, 1f, 1f] });

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.Equal(CommentScreeningClass.Firm, result.Class);
    }

    [Fact]
    public async Task ClassifyAsync_ComputesCentroidsOnceAndCachesAcrossCalls()
    {
        var screener = BuildScreener(out var generator, overrides: new Dictionary<string, float[]> { ["comment"] = [1f, 0f, 0f] });

        await screener.ClassifyAsync("comment", ClientId);
        await screener.ClassifyAsync("comment", ClientId);

        // The multi-input exemplar batch is embedded exactly once; only the single comment is re-embedded per call.
        await generator.Received(1).GenerateAsync(
            Arg.Is<IEnumerable<string>>(values => values.Count() > 1),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_WithoutResolver_ReturnsDegraded()
    {
        var screener = new EmbeddingSemanticCommentScreener(Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()));

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.True(result.IsDegraded);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyClientId_ReturnsDegraded()
    {
        var screener = BuildScreener(out _, overrides: new Dictionary<string, float[]> { ["comment"] = [1f, 0f, 0f] });

        var result = await screener.ClassifyAsync("comment", Guid.Empty);

        Assert.True(result.IsDegraded);
    }

    [Fact]
    public async Task ClassifyAsync_GeneratorThrows_ReturnsDegraded()
    {
        var screener = BuildScreener(out var generator, overrides: new Dictionary<string, float[]> { ["comment"] = [1f, 0f, 0f] });
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("embedding backend unavailable"));

        var result = await screener.ClassifyAsync("comment", ClientId);

        Assert.True(result.IsDegraded);
    }

    [Fact]
    public async Task ClassifyAsync_WhitespaceComment_ReturnsFirmNotDegraded()
    {
        var screener = BuildScreener(out _);

        var result = await screener.ClassifyAsync("   ", ClientId);

        Assert.Equal(CommentScreeningClass.Firm, result.Class);
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public void Exemplars_CoverEveryClass_WithNonEmptySets()
    {
        foreach (var screeningClass in Enum.GetValues<CommentScreeningClass>())
        {
            Assert.True(CommentScreeningExemplars.ByClass.TryGetValue(screeningClass, out var exemplars));
            Assert.NotEmpty(exemplars!);
        }
    }

    private static EmbeddingSemanticCommentScreener BuildScreener(
        out IEmbeddingGenerator<string, Embedding<float>> generator,
        double threshold = 0.5,
        Dictionary<string, float[]>? overrides = null)
    {
        var vectorOverrides = overrides ?? new Dictionary<string, float[]>(StringComparer.Ordinal);

        generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = texts.Select(text => new Embedding<float>(VectorFor(text, vectorOverrides))).ToArray();
                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });

        var connection = AiConnectionTestFactory.CreateEmbeddingConnection(ClientId);
        var binding = connection.PurposeBindings.Single(candidate => candidate.Purpose == AiPurpose.EmbeddingDefault);
        var model = connection.ConfiguredModels.Single(candidate => candidate.Id == binding.ConfiguredModelId);

        var runtime = Substitute.For<IResolvedAiEmbeddingRuntime>();
        runtime.Model.Returns(model);
        runtime.Generator.Returns(generator);

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveEmbeddingRuntimeAsync(ClientId, AiPurpose.EmbeddingDefault, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(runtime);

        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { CommentScreeningSimilarityThreshold = threshold });
        return new EmbeddingSemanticCommentScreener(options, resolver);
    }

    private static float[] VectorFor(string text, IReadOnlyDictionary<string, float[]> overrides)
    {
        if (overrides.TryGetValue(text, out var vector))
        {
            return vector;
        }

        foreach (var (screeningClass, exemplars) in CommentScreeningExemplars.ByClass)
        {
            if (exemplars.Contains(text, StringComparer.Ordinal))
            {
                return Direction(screeningClass);
            }
        }

        return Direction(CommentScreeningClass.Firm);
    }
}
